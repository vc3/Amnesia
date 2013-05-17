using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Amnesia
{
	public interface IParallelActivityThread : IDisposable
	{
		void Complete();
	}
}
