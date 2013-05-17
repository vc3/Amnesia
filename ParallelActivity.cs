using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Transactions;

namespace Amnesia
{
	class ParallelActivity : IParallelActivity
	{
		private SessionTracker tracker;
		private Amnesia.SessionTracker.ActivityInfo activity;
		public ParallelActivity(SessionTracker tracker)
		{
			this.tracker = tracker;
			this.activity = tracker.ParallelDependentActivityIdentified();
		}

		public IParallelActivityThread UsingThread()
		{
			return new ActivityThread(tracker, activity, Session.Transaction.DependentClone(DependentCloneOption.BlockCommitUntilComplete));
		}

		public void Dispose()
		{
			tracker.ParallelDependentActivityCompleted();
		}

		private class ActivityThread : IParallelActivityThread
		{
			private DependentTransaction transaction;
			private SessionTracker tracker;

			public ActivityThread(SessionTracker tracker, Amnesia.SessionTracker.ActivityInfo activity, DependentTransaction transaction)
			{
				this.tracker = tracker;
				this.transaction = transaction;

				tracker.ParallelDependentActivityStarted(activity);
			}

			public void Complete()
			{
				transaction.Complete();
			}

			public void Dispose()
			{
				try
				{
					transaction.Dispose();
				}
				finally
				{
					tracker.ParallelDependentActivityEnded();
				}
			}
		}
	}
}

