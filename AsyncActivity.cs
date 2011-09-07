using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Amnesia
{
	class AsyncActivity : IAsyncActivity
	{
		SessionTracker tracker;
		SessionTracker.ActivityInfo activity;

		public AsyncActivity(SessionTracker tracker)
		{
			this.tracker = tracker;
			this.activity = tracker.AsyncDependentActivityIdentified();
		}

		public void Starting()
		{
			tracker.AsyncDependentActivityStarted(activity);
		}

		public void Ended()
		{
			tracker.AsyncDependentActivityEnded();
		}
	}
}
