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

		class PropagationInfo
		{
			public int TotalThreads;
			public int AcquiredThreads;
			public ManualResetEvent AllAcquired;
			public int DoneThreads;
			public ManualResetEvent AllDone;
			public Action Action;
			public bool Canceled;
			public string Log;
			public ManualResetEvent Cancel;
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
		/// <param name="threadAcquisitionTimeoutMS">Time to wait before aborting to prevent deadlocks if greater than zero.  If specified,
		/// action might be performed multiple times on the same thread or not at all. Used only for async=false.</param>
		public static void ForAllThreads(Action action, string log)
		{
			ForAllThreads(action, log, 2000, 15);
		}

		/// <summary>
		/// Performs an action on every thread in the thread pool.
		/// </summary>
		/// <param name="action">action to perform on all threads</param>
		/// <param name="log">Message for debugging</param>
		/// <param name="threadAcquisitionTimeoutMS">Time to wait before aborting to prevent deadlocks if greater than zero.  If specified,
		/// action might be performed multiple times on the same thread or not at all. Used only for async=false.</param>
		public static void ForAllThreads(Action action, string log, int threadAcquisitionTimeoutMS, int maxRetries)
		{
			int retries = 0;

			while (!ForAllThreads(action, log, threadAcquisitionTimeoutMS))
			{
				++retries;

				if (retries == maxRetries)
				{
					throw new TimeoutException("Cannot perform action '" + log + "', retries = " + maxRetries);
				}
			}
		}

		/// <summary>
		/// Performs an action on every thread in the thread pool.
		/// </summary>
		/// <param name="action">action to perform on all threads</param>
		/// <param name="log">Message for debugging</param>
		/// <param name="threadAcquisitionTimeoutMS">Time to wait before aborting to prevent deadlocks if greater than zero.  If specified,
		/// action might be performed multiple times on the same thread or not at all. Used only for async=false.</param>
		/// <returns>False if the timeout occured</returns>
		public static bool ForAllThreads(Action action, string log, int threadAcquisitionTimeoutMS)
		{
			if (Thread.CurrentThread.IsThreadPoolThread)
				throw new InvalidOperationException("This method cannot be called from a thread pool thread");

			lock(mutext)
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
					TotalThreads = workerThreads + ioThreads,
					AllAcquired = new ManualResetEvent(false),
					AllDone = new ManualResetEvent(false),
					Cancel = new ManualResetEvent(false),
					Action = action,
					Log = log
				};

				NameThread("worker");
				Debug.WriteLine(string.Format("[thread {0}/{3}] workersThreads: {1}, ioThreads: {2}", Thread.CurrentThread.ManagedThreadId, workerThreads, ioThreads, Thread.CurrentThread.Name));

				// make the pending actions visible to other thread pool threads trying to get our mutex
				
				// saturate worker threads
				for (int i = 0; i < workerThreads; ++i)
					ThreadPool.QueueUserWorkItem(delegate
					{
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

				// wait for all thread pool threads to perform the action.
				if(!pendingSafe.AllAcquired.WaitOne(threadAcquisitionTimeoutMS))
				{
					// Timed out waiting for threads to be acquired. Must double check inside lock that not all threads have been acquired.
					lock (pendingSafe)
					{
						if (pendingSafe.AcquiredThreads < pendingSafe.TotalThreads)
						{
							// Cancel the threads that are waiting for the acquisition to complete
							pendingSafe.Canceled = true;
							pendingSafe.Cancel.Set();
							return false;
						}
					}
				}

				// All threads were acquired so wait for the action to complete.
				pendingSafe.AllDone.WaitOne();

				return true;
			}
		}

		private static void NameThread(string name)
		{
#if DEBUG
			if (string.IsNullOrEmpty(Thread.CurrentThread.Name))
				Thread.CurrentThread.Name = name + "_" + Thread.CurrentThread.ManagedThreadId;
#endif
		}

		static void DoPendingAction(PropagationInfo pendingSafe)
		{
			Debug.WriteLine(string.Format("[thread {0}] ->{1}", Thread.CurrentThread.Name, pendingSafe.Log));

			try
			{
				bool isLastThread;

				// Wait for all threads to be acquired before performing action so that retries can be implemented.
				lock (pendingSafe)
				{
					if (pendingSafe.Canceled)
						return;

					// update thread count
					++pendingSafe.AcquiredThreads;

					isLastThread = pendingSafe.TotalThreads == pendingSafe.AcquiredThreads;
				}

				if (isLastThread)
				{
					// notify other threads that they can proceed
					Debug.WriteLine(string.Format("[thread {0}] =>AllAcquired.Set", Thread.CurrentThread.Name, pendingSafe.Log));
					pendingSafe.AllAcquired.Set();
				}
				else
				{
					// wait for the remaining threads to be acquired, or until cancelled
					Debug.WriteLine(string.Format("[thread {0}] {1}  ->AllAcquired.WaitOne", Thread.CurrentThread.Name, pendingSafe.Log));

					if (WaitHandle.WaitAny(new WaitHandle[] { pendingSafe.Cancel, pendingSafe.AllAcquired }) == 0)
					{
						Debug.WriteLine(string.Format("[thread {0}] {1}  <= cancel", Thread.CurrentThread.Name, pendingSafe.Log));
						return;
					}

					Debug.WriteLine(string.Format("[thread {0}] {1}  <-AllAcquired.WaitOne", Thread.CurrentThread.Name, pendingSafe.Log));
				}

				// at this point, all threads in the pool have been acquired and the action can be performed

				Debug.WriteLine(string.Format("[thread {0}] {1} >>exec", Thread.CurrentThread.Name, pendingSafe.Log));
				try { pendingSafe.Action(); }
				catch { }
				Debug.WriteLine(string.Format("[thread {0}] {1} <<exec", Thread.CurrentThread.Name, pendingSafe.Log));

				lock (pendingSafe)
				{
					// update thread count
					++pendingSafe.DoneThreads;
					isLastThread = pendingSafe.TotalThreads == pendingSafe.DoneThreads;
				}

				if (isLastThread)
				{
					// notify other threads that the action is completed
					Debug.WriteLine(string.Format("[thread {0}] {1} =>AllDone.Set", Thread.CurrentThread.Name, pendingSafe.Log));
					pendingSafe.AllDone.Set();
				}
				else
				{
					// wait for other threads to finish the action
					Debug.WriteLine(string.Format("[thread {0}] {1}  ->AllDone.WaitOne", Thread.CurrentThread.Name, pendingSafe.Log));
					pendingSafe.AllDone.WaitOne();
					Debug.WriteLine(string.Format("[thread {0}] {1}  <-AllDone.WaitOne", Thread.CurrentThread.Name, pendingSafe.Log));
				}
			}
			finally
			{
				Debug.WriteLine(string.Format("[thread {0}] <-{1}", Thread.CurrentThread.Name, pendingSafe.Log));
			}
		}
	}
}
