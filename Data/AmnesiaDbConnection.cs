using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data;
using System.Data.Common;

namespace Amnesia.Data
{
	public class AmnesiaDbConnection : IDbConnection
	{
		DbConnection real;

		internal AmnesiaDbConnection(System.Transactions.Transaction distributedTransaction, DbConnection real)
		{
			this.real = real;

			// join the transaction
			real.Open();
			real.EnlistTransaction(distributedTransaction);
		}

		public AmnesiaDbTransaction Transaction
		{
			get;
			internal set;
		}

		public IDbTransaction BeginTransaction(IsolationLevel il)
		{
			return new AmnesiaDbTransaction()
			{ 
				Connection = this,
				IsolationLevel = il
			};
		}

		public IDbTransaction BeginTransaction()
		{
			return BeginTransaction(IsolationLevel.Serializable);
		}

		public void ChangeDatabase(string databaseName)
		{
			real.ChangeDatabase(databaseName);
		}

		public void Close()
		{
			// keep the real connection open
		}

		internal IDbConnection Real
		{
			get
			{
				return real;
			}
		}

		public string ConnectionString
		{
			get
			{
				return real.ConnectionString;
			}
			set
			{
				real.ConnectionString = value;
			}
		}

		public int ConnectionTimeout
		{
			get { return real.ConnectionTimeout; }
		}

		public IDbCommand CreateCommand()
		{
			return new AmnesiaDbCommand(real.CreateCommand())
			{
 				Connection = this
			};
		}

		public string Database
		{
			get { return real.Database;  }
		}

		public void Open()
		{
			real.Open();
		}

		public ConnectionState State
		{
			get { return real.State; }
		}

		public void Dispose()
		{
			real.Dispose();
		}
	}
}
