using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

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
						ForAllThreads(false, () => { });
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

		public static void ForAllThreads(bool async, Action action)
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
				int totalThreads;
				int ioThreads;
				ThreadPool.GetMaxThreads(out totalThreads, out ioThreads);

				// track progress in a static field so that if
				// another thread from the pool is already busy and will eventually
				// call ForAllThreads, it can go ahead and do this action too rather
				// than deadlock.
				var pendingSafe = new PropagationInfo() {
					Remaining = totalThreads, 
					Action = action,
					AllDone = new ManualResetEvent(false),
					Async = async
				};

				// make the pending actions visible to other thread pool threads trying to get our mutex
				pending = pendingSafe;

				// the pool
				for (int i = 0; i < totalThreads-1; ++i)
					ThreadPool.QueueUserWorkItem(delegate { DoPendingAction(pendingSafe); });

				// The current thread. This will block this thread until
				// all other threads in the pool have performed the action too.
				DoPendingAction(pendingSafe);
			}
			finally
			{
				Monitor.Exit(mutext);
			}
		}

		static void DoPendingAction(PropagationInfo pendingSafe)
		{
			// action first, then block
			if (!pendingSafe.Async)
			{
				try { pendingSafe.Action(); }
				catch { }
			}

			if (Interlocked.Decrement(ref pendingSafe.Remaining) > 0)
				pendingSafe.AllDone.WaitOne();
			else
				pendingSafe.AllDone.Set();

			// when async == true, still wait for thread pool to become saturated before proceeding
			// in order to ensure action is performed on all threads.
			// also, perform the action AFTER waiting to prevent deadlocks.
			if (pendingSafe.Async)
			{
				try { pendingSafe.Action(); }
				catch { }
			}
		}
	}
}
