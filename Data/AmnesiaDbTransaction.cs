using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data;

namespace Amnesia.Data
{
	public class AmnesiaDbTransaction : IDbTransaction
	{
		public void Commit()
		{
		}

		public IDbConnection Connection
		{
			get;
			internal set;
		}

		public IsolationLevel IsolationLevel
		{
			get;
			internal set;
		}

		public void Rollback()
		{
		}

		public void Dispose()
		{
		}
	}
}
