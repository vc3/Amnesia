using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Amnesia
{
	class NullAsyncActivity : IAsyncActivity
	{
		public static IAsyncActivity Instance = new NullAsyncActivity();

		private NullAsyncActivity() { }
		public void Starting() { }
		public void Ended() { }
	}
}
