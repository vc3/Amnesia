using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;

namespace Amnesia
{
	/// <summary>
	/// Utility class for dealing with the .NET ThreadPool.
	/// </summary>
	static class ThreadUtil
	{
		/// <summary>
		/// ThreadPool threads will be held to this number of threads
		/// </summary>
		const int WORKER_THREADS = 10;

		/// <summary>
		/// ThreadPool IO threads will be held to this number of threads
		/// </summary>
		const int IO_THREADS = 10;

		static bool reducedThreads;
		static object mutext = new object();
		static Thread keepAliveThread;
		static PropagationInfo pending;

		class PropagationInfo
		{
			public int Remaining;
			public Action Action;
			public ManualResetEvent AllDone;
			public bool Timedout;
			public string Log;
			public int AccessTimeoutMs;
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
						ForAllThreads(() => { }, "keep alive", 2000);
						Thread.Sleep(1000);
					}
				});
				keepAliveThread.Name = "Amnesia thread pool keep alive";
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

		/// <summary>
		/// Performs an action on every thread in the thread pool.
		/// </summary>
		/// <param name="action">action to perform on all threads</param>
		/// <param name="log">Message for debugging</param>
		/// <param name="poolAccessTimeoutMs">Time to wait before aborting to prevent deadlocks if greater than zero.  If specified,
		/// action might be performed multiple times on the same thread or not at all. Used only for async=false.</param>
		public static void ForAllThreads(Action action, string log)
		{
			ForAllThreads(action, log, 0);
		}


		/// <summary>
		/// Performs an action on every thread in the thread pool.
		/// </summary>
		/// <param name="action">action to perform on all threads</param>
		/// <param name="log">Message for debugging</param>
		/// <param name="poolAccessTimeoutMs">Time to wait before aborting to prevent deadlocks if greater than zero.  If specified,
		/// action might be performed multiple times on the same thread or not at all. Used only for async=false.</param>
		static void ForAllThreads(Action action, string log, int poolAccessTimeoutMs)
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
				if (!reducedThreads)
				{
					// Reduce the number of threads in the pool so transaction propagates faster
					ThreadPool.SetMaxThreads(WORKER_THREADS, IO_THREADS);

					// Prevent threads from being killed.  Under .NET 3.5 This must be done in conjunction with
					// the keep alive thread to periodically use the threads in the pool.  If minimum isn't set threads
					// are ended unexpectedly and when keep alive stops, the threads are killed when observed through
					// the debugger.
					// TODO: Verify this works under .NET 4
					ThreadPool.SetMinThreads(WORKER_THREADS, IO_THREADS);

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
					Log = log,
					AccessTimeoutMs = poolAccessTimeoutMs
				};

				NameThread("worker");
				Debug.WriteLine(string.Format("[thread {0}/{3}] workersThreads: {1}, ioThreads: {2}", Thread.CurrentThread.ManagedThreadId, workerThreads, ioThreads, Thread.CurrentThread.Name));

				// make the pending actions visible to other thread pool threads trying to get our mutex
				pending = pendingSafe;

				// saturate worker threads
				// is the current thread already a worker?
				if (Thread.CurrentThread.IsThreadPoolThread)
					--workerThreads;

				for (int i = 0; i < workerThreads; ++i)
					ThreadPool.QueueUserWorkItem(delegate {
						NameThread("worker");
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
								NameThread("io");
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
				DoPendingAction(pendingSafe);
				pending = null;
			}
			finally
			{
				Monitor.Exit(mutext);
			}
		}

		private static void NameThread(string name)
		{
#if DEBUG
			if (string.IsNullOrEmpty(Thread.CurrentThread.Name))
				Thread.CurrentThread.Name = name + "_" + Thread.CurrentThread.ManagedThreadId;
#endif
		}

		static bool DoPendingAction(PropagationInfo pendingSafe)
		{
			Debug.WriteLine(string.Format("[thread {0}] ->{1}", Thread.CurrentThread.Name, pendingSafe.Log));

			try
			{
				// action first, then block
				Debug.WriteLine(string.Format("[thread {0}] {1} >>exec:sync", Thread.CurrentThread.Name, pendingSafe.Log));
				try { pendingSafe.Action(); }
				catch { }

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
					Debug.WriteLine(string.Format("[thread {0}] {1}  ->AllDone.WaitOne", Thread.CurrentThread.Name, pendingSafe.Log));

					if (pendingSafe.AccessTimeoutMs <= 0)
						pendingSafe.AllDone.WaitOne();
					else if (!pendingSafe.AllDone.WaitOne(pendingSafe.AccessTimeoutMs))
						return false;

					Debug.WriteLine(string.Format("[thread {0}] {1}  <-AllDone.WaitOne", Thread.CurrentThread.Name, pendingSafe.Log));
				}
				else
				{
					Debug.WriteLine(string.Format("[thread {0}] {1} =>AllDone.Set", Thread.CurrentThread.Name, pendingSafe.Log));
					pendingSafe.AllDone.Set();
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
