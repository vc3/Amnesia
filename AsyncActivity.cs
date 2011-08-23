using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Amnesia
{
	class AsyncActivity : IAsyncActivity
	{
		SessionTracker tracker;

		public AsyncActivity(SessionTracker tracker)
		{
			this.tracker = tracker;
			tracker.AsyncActivityIdentified();
		}

		public void Starting()
		{
			tracker.AsyncActivityStarted();
		}

		public void Ended()
		{
			tracker.AsyncActivityEnded();
		}
	}
}
