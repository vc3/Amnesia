using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Amnesia
{
	class NullParallelActivity : IParallelActivity
	{
		public static IParallelActivity Instance = new NullParallelActivity();
		private IParallelActivityThread threadActivity = new ThreadActivity();

		private NullParallelActivity()
		{
		}

		public void Dispose()
		{
		}

		public IParallelActivityThread UsingThread()
		{
			return threadActivity;
		}

		private class ThreadActivity : IParallelActivityThread
		{
			public void Dispose()
			{
			}

			public void Complete()
			{
			}
		}
	}
}
