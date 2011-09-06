using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Amnesia
{
	/// <summary>
	/// Implementation of ILog that adapts the interface to a delegate.
	/// </summary>
	public class LogAdapter : ILog
	{
		Action<string, object[]> write;

		public LogAdapter(Action<string, object[]> write)
		{
			this.write = write;
		}

		public void Write(string messageFormat, params object[] args)
		{
			write(messageFormat, args);
		}

		public void CopyInto(ILog log)
		{
			throw new NotImplementedException();
		}
	}
}
