using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;

namespace Amnesia
{
	static class ThreadUtil
	{
		static bool reducedThreads;
		static object mutext = new object();
		static Thread keepAliveThread;
		static PropagationInfo pending;

		class PropagationInfo
		{
			public int Remaining;
			public Action Action;
			public ManualResetEvent AllDone;
			public bool Async;
			public bool Timedout;
			public string Log;
		}

		/// <summary>
		/// Prevents threads in the thread from remaining idle so they are not killed
		/// </summary>
		public static void StartThreadPoolKeepAlive()
		{
			lock (mutext)
			{
				if (keepAliveThread != null)
					return;

				keepAliveThread = new Thread(delegate()
				{
					Thread.Sleep(1000);
					while (keepAliveThread != null)
					{
						ForAllThreads(false, () => { }, "keep alive");
						Thread.Sleep(1000);
					}
				});

				keepAliveThread.Start();
			}
		}

		/// <summary>
		/// Stops work that keeps threads from being idle so they can be killed
		/// </summary>
		public static void StopThreadPoolKeepAlive()
		{
			if (keepAliveThread == null)
				return;

			keepAliveThread.Abort();
			keepAliveThread = null;
		}
		public static void ForAllThreads(bool async, Action action, string log)
		{
			List<PropagationInfo> completed = null;

			// must lock to prevent two threads from trying to saturate the thread pool at
			// the same time and deadlocking
			while (!Monitor.TryEnter(mutext, 100))
			{
				// If the lock cannot be acquired it might be because this
				// thread is a thread from the ThreadPool and we might
				// be in a deadlock sitation. To prevent deadlock, let this
				// thread perform the pending action as well.
				if (pending != null && Thread.CurrentThread.IsThreadPoolThread)
				{
					if (completed == null)
						completed = new List<PropagationInfo>();

					if (!completed.Contains(pending))
					{
						// don't perform an action twice (due to race condition after DoPendingAction)
						completed.Add(pending);

						// Execute the pending action that another thread is waiting on this one to complete.
						// This will block the current thread until the action has been executed by all
						// other threads.
						DoPendingAction(pending);
					}
				}
			}
			try
			{
				// Reduce the number of threads in the pool so transaction propagates faster
				if (!reducedThreads)
				{
					ThreadPool.SetMaxThreads(10, 10);
					reducedThreads = true;
				}

				// then run on all threads in the thread pool
				int workerThreads;
				int ioThreads;
				ThreadPool.GetMaxThreads(out workerThreads, out ioThreads);

				// track progress in a static field so that if
				// another thread from the pool is already busy and will eventually
				// call ForAllThreads, it can go ahead and do this action too rather
				// than deadlock.
				var pendingSafe = new PropagationInfo() {
					Remaining = workerThreads + ioThreads, 
					Action = action,
					AllDone = new ManualResetEvent(false),
					Async = async,
					Log = log
				};

				//NameThread("leader");
				Debug.WriteLine(string.Format("[thread {0}/{3}] workersThreads: {1}, ioThreads: {2}", Thread.CurrentThread.ManagedThreadId, workerThreads, ioThreads, Thread.CurrentThread.Name));

				// make the pending actions visible to other thread pool threads trying to get our mutex
				pending = pendingSafe;

				// saturate worker threads
				// is the current thread already a worker?
				if (Thread.CurrentThread.IsThreadPoolThread)
					--workerThreads;

				for (int i = 0; i < workerThreads; ++i)
					ThreadPool.QueueUserWorkItem(delegate {
						//NameThread("worker");
						DoPendingAction(pendingSafe); 
					});

				// saturate i/o threads
				for (int i = 0; i < ioThreads; ++i)
				{
					unsafe
					{
						Overlapped overlapped = new Overlapped();
						NativeOverlapped* pOverlapped = overlapped.Pack((uint errorCode, uint numBytes, NativeOverlapped* _overlapped) =>
						{
							try
							{
								//NameThread("io");
								DoPendingAction(pendingSafe);
							}
							finally
							{
								Overlapped.Free(_overlapped);
							}
						}, null);

						ThreadPool.UnsafeQueueNativeOverlapped(pOverlapped);
					}
				}

				// The current thread. This will block this thread until
				// all other threads in the pool have performed the action too.
				if (!DoPendingAction(pendingSafe))
				{
					// Action could not be performed due to a timeout. Requeue the call on a
					// new thread and try again later. This should only occur when async=true.
					// WARNING: This is somewhat risky in that actions might occur in an unexpected order.
					Debug.WriteLine(string.Format("[thread {0}/{2}] {1}", Thread.CurrentThread.ManagedThreadId, "Queuing action for retry", Thread.CurrentThread.Name));
					pending = null;
					ThreadPool.QueueUserWorkItem(delegate {
						ForAllThreads(async, action, log);
					});
				}
				else
				{
					pending = null;
				}
			}
			finally
			{
				Monitor.Exit(mutext);
			}
		}

		//private static void NameThread(string name)
		//{
		//    Thread.CurrentThread.Name = Thread.CurrentThread.ManagedThreadId + "-" + name;
		//}

		static bool DoPendingAction(PropagationInfo pendingSafe)
		{
			Debug.WriteLine(string.Format("[thread {0}] ->{1}", Thread.CurrentThread.Name, pendingSafe.Log));

			try
			{
				// action first, then block
				if (!pendingSafe.Async)
				{
					Debug.WriteLine(string.Format("[thread {0}] {1} >>exec:sync", Thread.CurrentThread.Name, pendingSafe.Log));
					try { pendingSafe.Action(); }
					catch { }
				}

				bool isLastThread;

				lock (pendingSafe)
				{
					// stop if we've timed out (async mode only)
					if (pendingSafe.Timedout)
						return false;

					// update thread count
					--pendingSafe.Remaining;
					isLastThread = pendingSafe.Remaining == 0;
				}

				if (!isLastThread)
				{
					if (pendingSafe.Async)
					{
						// if the task can be performed asynchrously then allow
						// it to timeout to work around deadlocks.
						Debug.WriteLine(string.Format("[thread {0}] {1}  ->AllDone.WaitOne(timeout)", Thread.CurrentThread.Name, pendingSafe.Log));
						if (!pendingSafe.AllDone.WaitOne(2000))
						{
							Debug.WriteLine(string.Format("[thread {0}] {1}  timeout<-AllDone.WaitOne(timeout)", Thread.CurrentThread.Name, pendingSafe.Log));
							// did the final thread
							int remaining;
							lock (pendingSafe)
							{
								remaining = pendingSafe.Remaining;
								pendingSafe.Timedout = (remaining > 0);
							}

							// Most likely, we're hung. However there's a chance that the final thread
							// came through at the moment. If there are no remaining threads then keep on
							// going and run the action.
							if (remaining > 0)
							{
								Debug.WriteLine(string.Format("[thread {0}] {1} !!TIMEOUT!!", Thread.CurrentThread.Name, pendingSafe.Log));
								return false;
							}
						}
						Debug.WriteLine(string.Format("[thread {0}] {1}  signaled<-AllDone.WaitOne(timeout)", Thread.CurrentThread.Name, pendingSafe.Log));
					}
					else
					{
						Debug.WriteLine(string.Format("[thread {0}] {1}  ->AllDone.WaitOne", Thread.CurrentThread.Name, pendingSafe.Log));
						pendingSafe.AllDone.WaitOne();
						Debug.WriteLine(string.Format("[thread {0}] {1}  <-AllDone.WaitOne", Thread.CurrentThread.Name, pendingSafe.Log));
					}
				}
				else
				{
					Debug.WriteLine(string.Format("[thread {0}] {1} =>AllDone.Set", Thread.CurrentThread.Name, pendingSafe.Log));
					pendingSafe.AllDone.Set();
				}

				// when async == true, still wait for thread pool to become saturated before proceeding
				// in order to ensure action is performed on all threads.
				// also, perform the action AFTER waiting to prevent deadlocks.
				if (pendingSafe.Async)
				{
					Debug.WriteLine(string.Format("[thread {0}] {1} >>exec:Async", Thread.CurrentThread.Name, pendingSafe.Log));
					try { pendingSafe.Action(); }
					catch { }
				}

				return true;
			}
			finally
			{
				Debug.WriteLine(string.Format("[thread {0}] <-{1}", Thread.CurrentThread.Name, pendingSafe.Log));
			}
		}
	}
}
