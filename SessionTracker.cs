using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web;

namespace Amnesia
{
	class SessionTracker
	{
		static Guid NO_GROUP = new Guid();

		[ThreadStatic]
		static Guid threadGroup;
		object THREAD_GROUP = new object();		

		object fieldsLock = new object();
		object pauseLock = new object();
		int activitiesExecuting = 0;
		int freeActivities = -1;
		bool paused = false;
		AutoResetEvent activitiesDone;

		Guid activityGroup = NO_GROUP;

		internal void StartActivity()
		{
			lock (fieldsLock)
			{
				IncrementActivityCount();
				BindActivityToThread();
			}
		}

		/// <summary>
		/// Caller is responsible for synchronization
		/// </summary>
		void IncrementActivityCount()
		{
			// Abort all activities when paused
			if (paused)
				throw new InvalidOperationException("All new activities are paused");

			// Track that an activity has been started
			if (activitiesExecuting == freeActivities)
				activityGroup = Guid.NewGuid();

			++activitiesExecuting;
		}

		/// <summary>
		/// Caller is responsible for synchronization
		/// </summary>
		void BindActivityToThread()
		{
			// Track which threads that are associated to the session
			if (HttpContext.Current != null)
				HttpContext.Current.Items[THREAD_GROUP] = activityGroup;
			else
				threadGroup = activityGroup;
		}

		internal void EndActivity()
		{
			lock (fieldsLock)
			{
				// Track that a request is ending
				--activitiesExecuting;

				if (activitiesExecuting == 0)
					activityGroup = Guid.NewGuid();

				// disassociate thread from session
				if (HttpContext.Current != null)
					HttpContext.Current.Items[THREAD_GROUP] = NO_GROUP;
				else
					threadGroup = NO_GROUP;

				// Notify any interested thread that there are no more activities executing
				if (activitiesExecuting == freeActivities)
				{
					// change group id to disassociate all threads just in case
					activityGroup = Guid.NewGuid();
					
					if (activitiesDone != null)
						activitiesDone.Set();
				}

				else if (activitiesExecuting < freeActivities)
				{
					// DEADLOCK: The request that requested the lock has ended but all future activities are being
					// blocked so the lock can never be released!

					// Correctly written code should never get here but it seems like the best thing to do is to
					// release the lock to prevent a deadlock.
					Resume();
				}
			}
		}

		internal void AsyncActivityIdentified()
		{
			lock (fieldsLock)
				IncrementActivityCount();
		}

		internal void AsyncActivityStarted()
		{
			lock (fieldsLock)
				BindActivityToThread();
		}

		internal void AsyncActivityEnded()
		{
			EndActivity();
		}

		/// <summary>
		/// Allow new activites to start
		/// </summary>
		private void Resume()
		{
			lock (fieldsLock)
			{
				paused = false;
				freeActivities = 0;

				// release the lock
				Monitor.Exit(pauseLock);
			}
		}

		/// <summary>
		/// Stop all incoming activities and wait for the current activities to complete. When
		/// this method returns, all activities (execpt for the current Amnesia one) will have
		/// completed and any future activities will be denied.
		/// </summary>
		private void Pause(int timeoutMS, bool inActivity)
		{
			// Ensure only a single thread owns the pause lock.
			Monitor.Enter(pauseLock);

			try
			{
				// At this point the current thread has been granted the pauseLock and can start blocking future activities as well as
				// wait for any current activities to complete.

				lock (fieldsLock)
				{
					// stop future activities from starting
					paused = true;

					// Take into account that the lock request may be occuring either from an existing activity
					freeActivities = inActivity ? 1 : 0;

					// Wait for any activities that are currently executing to complete
					if (activitiesExecuting > freeActivities)
					{
						// Let other threads know we're interested in knowing when we're idle and wait
						activitiesDone = new AutoResetEvent(false);
					}
				}

				// Wait for other request threads to complete
				if (activitiesDone != null)
				{
					activitiesDone.WaitOne();
					activitiesDone = null;
				}

				// Hold the pauseLock open until ResumeRequests()
			}
			catch (Exception)
			{
				// An unexpected error occured so release the pauseLock
				Resume();
				throw;
			}
		}

		/// <summary>
		/// Stop all new activities and wait for the current ones to complete. When
		/// this method returns, all activities (execpt for the current Amnesia one) will have
		/// completed and any future activities will be denied.
		/// </summary>
		internal IDisposable Exclusive(int timeoutMS, bool inActivity, ILog log)
		{
			log = log ?? NullLog.Instance;

			log.Write("Acquiring session lock...");
			Pause(timeoutMS, inActivity);
			log.Write("> Lock acquired");

			return new UndoableAction(delegate
			{
				log.Write("Releasing session lock...");
				Resume();
				log.Write("> Lock released");
			});
		}

		/// <summary>
		/// Throws an exception if the current thread is not tied to an activity
		/// </summary>
		internal void AssertActivityStarted()
		{
			lock (fieldsLock)
			{
				// disassociate thread from session
				if (HttpContext.Current != null)
				{
					if (!activityGroup.Equals(HttpContext.Current.Items[THREAD_GROUP]))
						throw new InvalidOperationException("The web request is not associated with the Amnesia session.  Call Session.StartActivity() first.");
				}
				else if (!activityGroup.Equals(threadGroup))
				{
					throw new InvalidOperationException("Thread " + Thread.CurrentThread.ManagedThreadId + " (" + Thread.CurrentThread.Name + ") is not associated with the Amnesia session.   Call Session.StartActivity() first.");
				}
			}
		}
	}
}
