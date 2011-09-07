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
		[ThreadStatic]
		static ActivityInfo threadActivity;
		object THREAD_ACTIVITY = new object();		

		object fieldsLock = new object();
		object pauseLock = new object();
		int activityCount = 0;
		bool paused = false;
		AutoResetEvent activitiesDone;
		int pendingActivities;

		internal class ActivityInfo
		{
			public Guid ID;
			public int ThreadCount = 1;
		}

		ActivityInfo CurrentThreadActivity
		{
			get
			{
				if (HttpContext.Current != null)
					return (ActivityInfo)HttpContext.Current.Items[THREAD_ACTIVITY];
				else
					return threadActivity;
			}
			set
			{
				if (HttpContext.Current != null)
					HttpContext.Current.Items[THREAD_ACTIVITY] = value;
				else
					threadActivity = value;
			}
		}

		#region Activity start/stop
		public void StartActivity()
		{
			lock (fieldsLock)
			{
				EnsureNotPaused();

				if (CurrentThreadActivity != null)
					throw new InvalidOperationException("An activity has already been started");

				CurrentThreadActivity = new ActivityInfo() { ID = Guid.NewGuid() };
				++activityCount;
			}
		}

		/// <summary>
		/// Ends an activity started with StartActivity or AsyncActivityStarted
		/// </summary>
		public void EndActivity()
		{
			lock (fieldsLock)
			{
				DecrementActivityCount();
			}
		}
		#endregion

		#region Async dependent activities
		/// <summary>
		/// Called by the thread that is initiating an async activity on a second thread.
		/// </summary>
		public ActivityInfo AsyncDependentActivityIdentified()
		{
			var activity = CurrentThreadActivity;
			if (activity == null)
				throw new InvalidOperationException("Expected to join into an existing activity but there is not one");

			lock (fieldsLock)
			{
				++activity.ThreadCount;
			}

			return activity;
		}

		/// <summary>
		/// Called by the thread executing the async activity
		/// </summary>
		public void AsyncDependentActivityStarted(ActivityInfo parentActivity)
		{
			// Associate the new thread to the parent activity
			CurrentThreadActivity = parentActivity;
		}

		/// <summary>
		/// Called by the async thread when its activity is complete
		/// </summary>
		public void AsyncDependentActivityEnded()
		{
			lock (fieldsLock)
			{
				DecrementActivityCount();
			}
		}
		#endregion

		#region Exclusive locking
		/// <summary>
		/// Stop all new activities and wait for the current ones to complete. When
		/// this method returns, all activities (execpt for the current Amnesia one) will have
		/// completed and any future activities will be denied.
		/// </summary>
		public IDisposable Exclusive(int timeoutMS, ILog log)
		{
			Pause(timeoutMS);

			return new UndoableAction(delegate
			{
				Resume();
			});
		}

		/// <summary>
		/// Throws an exception if the current thread is not tied to an activity
		/// </summary>
		public void AssertActivityStarted()
		{
			if (CurrentThreadActivity == null)
			{
				if (HttpContext.Current != null)
					throw new InvalidOperationException("The web request is not associated with the Amnesia session.  Call Session.StartActivity() first.");

				throw new InvalidOperationException("Thread " + Thread.CurrentThread.ManagedThreadId + " (" + Thread.CurrentThread.Name + ") is not associated with the Amnesia session.   Call Session.StartActivity() first.");
			}
		}
		#endregion

		#region Private methods
		/// <summary>
		/// Caller is responsible for synchronization
		/// </summary>
		private void EnsureNotPaused()
		{
			// Abort all activities when paused
			if (paused)
				throw new InvalidOperationException("All new activities are paused");
		}

		/// <summary>
		/// Called when an activity is completed. Both async and sync activities.
		/// </summary>
		private void DecrementActivityCount()
		{
			// ASSERT: one activity is executing
			if (activityCount == 0)
				throw new InvalidOperationException("Unbalanced call to EndActivity");

			// ASSERT: at least one thread is bound to the current activity
			var activity = CurrentThreadActivity;
			if (activity == null)
				throw new InvalidOperationException("Unbalanced call to EndActivity");

			CurrentThreadActivity = null;

			if (activity.ThreadCount == 0)
				throw new InvalidOperationException("Unbalanced call to EndActivity");


			// Have all threads completed work on this activity?
			--activity.ThreadCount;

			if (activity.ThreadCount == 0)
			{
				// activity is complete
				--activityCount;

				// Notify any interested thread that there are no more activities executing
				if (activitiesDone != null)
				{
					--pendingActivities;

					if(pendingActivities == 0)
						activitiesDone.Set();
				}
			}
		}

		/// <summary>
		/// Stop all incoming activities and wait for the current activities to complete. When
		/// this method returns, all activities (except for the current Amnesia one) will have
		/// completed and any future activities will be denied.
		/// </summary>
		private void Pause(int timeoutMS)
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

					// Wait for any activities that are currently executing to complete
					int freeActivities = (CurrentThreadActivity != null ? 1 : 0);

					if (activityCount > freeActivities)
					{
						// Let other threads know we're interested in knowing when we're idle and wait
						activitiesDone = new AutoResetEvent(false);

						// Take into account that the lock request may be occuring either from an existing activity
						pendingActivities = activityCount - freeActivities;
					}
				}

				// Wait for other request threads to complete
				if (activitiesDone != null)
				{
					activitiesDone.WaitOne();
					activitiesDone = null;
				}

				// Hold the pauseLock open until Resume()
			}
			catch (Exception)
			{
				// An unexpected error occured so release the pauseLock
				Resume();
				throw;
			}
		}

		/// <summary>
		/// Allow new activites to start
		/// </summary>
		private void Resume()
		{
			lock (fieldsLock)
			{
				paused = false;

				// release the lock
				Monitor.Exit(pauseLock);
			}
		}
		#endregion
	}
}
