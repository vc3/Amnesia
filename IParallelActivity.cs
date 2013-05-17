using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Amnesia
{
	public interface IParallelActivity : IDisposable
	{
		/// <summary>
		/// Call at the start of a new thread before performing actions.
		/// </summary>
		/// <returns></returns>
		IParallelActivityThread UsingThread();
	}
}
