using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Amnesia
{
	/// <summary>
	/// Discards logged messages
	/// </summary>
	class NullLog : ILog
	{
		public static readonly ILog Instance = new NullLog();

		private NullLog()
		{
		}

		public void Write(string messageFormat, params object[] args)
		{
		}

		public void CopyInto(ILog log)
		{
		}
	}
}
