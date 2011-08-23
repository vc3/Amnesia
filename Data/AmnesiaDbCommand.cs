using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data;

namespace Amnesia.Data
{
	public class AmnesiaDbCommand : IDbCommand
	{
		IDbCommand real;
		AmnesiaDbConnection connection;

		public AmnesiaDbCommand(IDbCommand real)
		{
			this.real = real;
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
			return real.ExecuteNonQuery();
		}

		public IDataReader ExecuteReader(CommandBehavior behavior)
		{
			return new DisconnectedReader(real.ExecuteReader(behavior));
		}

		public IDataReader ExecuteReader()
		{
			return new DisconnectedReader(real.ExecuteReader());
		}

		public object ExecuteScalar()
		{
			return real.ExecuteScalar();
		}

		public IDataParameterCollection Parameters
		{
			get { return real.Parameters; }
		}

		public void Prepare()
		{
			real.Prepare();
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
