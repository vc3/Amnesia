using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Amnesia
{
	/// <summary>
	/// An activity that is being performed that is part of the current session.
	/// </summary>
	public interface IAsyncActivity
	{
		void Starting();
		void Ended();
		void WaitUntilEnded(int milliseconds);
	}
}
