using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Amnesia
{
	class AsyncActivity : IAsyncActivity
	{
		SessionTracker tracker;
		SessionTracker.ActivityInfo activity;
		ManualResetEvent doneEvent = new ManualResetEvent(false);

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
			try
			{
				tracker.AsyncDependentActivityEnded();
			}
			finally
			{
				doneEvent.Set();
			}
		}

		public void WaitUntilEnded(int milliseconds)
		{
			doneEvent.WaitOne(milliseconds);
		}
	}
}
