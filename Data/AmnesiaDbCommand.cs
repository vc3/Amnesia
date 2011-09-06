using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data;
using System.Threading;
using System.Diagnostics;

namespace Amnesia.Data
{
	public class AmnesiaDbCommand : IDbCommand
	{
		IDbCommand real;
		AmnesiaDbConnection connection;
		DateTime executionStartTime;
		bool deadlockSuspected = false;
		Thread executingThread = null;

		static IList<AmnesiaDbCommand> executing = new List<AmnesiaDbCommand>();
		static Timer deadlockCheckTimer;

		const long DEADLOCK_CHECK_PERIOD_MS = 1000;
		const long DEADLOCK_WARNING_MS = 10000;

		public AmnesiaDbCommand(IDbCommand real)
		{
			this.real = real;
		}

		private void StartDeadlockMonitor()
		{
			executionStartTime = DateTime.Now;
			deadlockSuspected = false;

			lock (executing)
			{
				executing.Add(this);

				if (deadlockCheckTimer == null)
					deadlockCheckTimer = new Timer(CheckForDeadlocks, null, DEADLOCK_CHECK_PERIOD_MS, DEADLOCK_CHECK_PERIOD_MS);
			}
		}

		private void StopDeadlockMonitor()
		{
			lock (executing)
			{
				executing.Remove(this);
				executingThread = null;
				deadlockSuspected = false;
			}
		}

		static void CheckForDeadlocks(object arg)
		{
			lock (executing)
			{
				bool newSuspects = false;
				List<AmnesiaDbCommand> suspects = null;

				foreach (var cmd in executing)
				{
					if ((DateTime.Now - cmd.executionStartTime).TotalMilliseconds > DEADLOCK_WARNING_MS && !cmd.deadlockSuspected)
					{
						if (!cmd.deadlockSuspected)
						{
							cmd.deadlockSuspected = true;
							newSuspects = true;
						}

						if (suspects == null)
							suspects = new List<AmnesiaDbCommand>();

						suspects.Add(cmd);
					}
				}

				if (newSuspects)
				{
					for (int i = 0; i < suspects.Count; ++i)
					{
						AmnesiaDbCommand cmd = suspects[i];

						if (i == 0)
							Session.AsyncLog.Write("These commands have been executing for a long time and may be deadlocked:");

						Session.AsyncLog.Write("#{2} [+{0}ms] {1}", (DateTime.Now - cmd.executionStartTime).TotalMilliseconds, cmd.CommandText, i + 1);
						Session.AsyncLog.Write(new StackTrace(cmd.executingThread, true).ToString());
					}
				}
			}
		}

		public void Cancel()
		{
			real.Cancel();
		}

		public string CommandText
		{
			get
			{
				return real.CommandText;
			}
			set
			{
				real.CommandText = value;
			}
		}

		public int CommandTimeout
		{
			get
			{
				return real.CommandTimeout;
			}
			set
			{
				real.CommandTimeout = value;
			}
		}

		public CommandType CommandType
		{
			get
			{
				return real.CommandType;
			}
			set
			{
				real.CommandType = value;
			}
		}

		public IDbConnection Connection
		{
			get
			{
				return connection;
			}
			set
			{
				connection = (AmnesiaDbConnection)value;
				real.Connection = connection.Real;
			}
		}

		public IDbDataParameter CreateParameter()
		{
			return real.CreateParameter();
		}

		public int ExecuteNonQuery()
		{
			StartDeadlockMonitor();
			try
			{
				return real.ExecuteNonQuery();
			}
			finally
			{
				StopDeadlockMonitor();
			}
		}

		public IDataReader ExecuteReader(CommandBehavior behavior)
		{
			StartDeadlockMonitor();
			try
			{
				return new DisconnectedReader(real.ExecuteReader(behavior));
			}
			finally
			{
				StopDeadlockMonitor();
			}
		}

		public IDataReader ExecuteReader()
		{
			StartDeadlockMonitor();
			try
			{
				return new DisconnectedReader(real.ExecuteReader());
			}
			finally
			{
				StopDeadlockMonitor();
			}
		}

		public object ExecuteScalar()
		{
			StartDeadlockMonitor();
			try
			{
				return real.ExecuteScalar();
			}
			finally
			{
				StopDeadlockMonitor();
			}
		}

		public IDataParameterCollection Parameters
		{
			get { return real.Parameters; }
		}

		public void Prepare()
		{
			StartDeadlockMonitor();
			try
			{
				real.Prepare();
			}
			finally
			{
				StopDeadlockMonitor();
			}
		}

		public IDbTransaction Transaction
		{
			get
			{
				return connection.Transaction;
			}
			set
			{
				connection.Transaction = (AmnesiaDbTransaction)value;
			}
		}

		public UpdateRowSource UpdatedRowSource
		{
			get
			{
				return real.UpdatedRowSource;
			}
			set
			{
				real.UpdatedRowSource = value;
			}
		}

		public void Dispose()
		{
			real.Dispose();
		}
	}
}
