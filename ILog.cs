using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Amnesia
{
	public interface ILog
	{
		void Write(string messageFormat, params object[] args);

		void CopyInto(ILog log);
	}
}
