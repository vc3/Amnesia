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

		class PropagationInfo
		{
			public int Remaining;
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
					while (true)
					{
						ForAllThreads(() => { });
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

		public static void ForAllThreads(Action action)
		{
			// must lock to prevent two threads from trying to saturate the thread pool at
			// the same time and deadlocking
			lock (mutext)
			{
				// Reduce the number of threads in the pool so transaction propagates faster
				if (!reducedThreads)
				{
					ThreadPool.SetMaxThreads(10, 10);
					reducedThreads = true;
				}

				// run on current thread
				try { action(); }
				catch { }

				// then run on all threads in the thread pool
				int otherThreads;
				int ioThreads;
				ThreadPool.GetMaxThreads(out otherThreads, out ioThreads);
				--otherThreads;

				var completed = new PropagationInfo() { Remaining = otherThreads };
				var done = new ManualResetEvent(false);

				for (int i = 0; i < otherThreads; ++i)
				{
					ThreadPool.QueueUserWorkItem(delegate
					{
						// wait for thread pool to become saturated before proceeding
						// in order to ensure action is performed on all threads.
						// also, perform the action AFTER waiting to prevent deadlocks.
						if (Interlocked.Decrement(ref completed.Remaining) > 0)
							done.WaitOne();
						else
							done.Set();

						// run on one of the threads
						try { action(); }
						catch { }

					});
				}

				// wait for action to be run on all threads
				done.WaitOne();
			}
		}
	}
}
