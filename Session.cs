using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Transactions;
using System.Threading;
using System.Data;
using System.Web;
using System.Data.Common;
using Amnesia.Data;

namespace Amnesia
{
	public class Session : IDisposable
	{
		static TimeSpan TransactionTimeout = TimeSpan.Zero;
		static EventHandler afterSessionEnded;

		static readonly object BOUND_KEY = new object();
		internal static SessionTracker Tracker = new SessionTracker();

		internal TransactionScope TxScope;

		string serviceUrl;
		EventHandler onAbortedAsync;
		bool wasAbortedAsync = false;
		private bool isDisposed;
		EventHandler onDisposed;
		ILog log;
		static Guid id = Guid.Empty;

		static Dictionary<string, AmnesiaDbConnection> connections = new Dictionary<string, AmnesiaDbConnection>();

		#region AbortNotification
		class AbortNotification : IEnlistmentNotification
		{
			EventHandler handler;

			public AbortNotification(EventHandler handler)
			{
				this.handler = handler;
			}

			public void Commit(Enlistment enlistment)
			{
				enlistment.Done();
			}

			public void InDoubt(Enlistment enlistment)
			{
				enlistment.Done();
			}

			public void Prepare(PreparingEnlistment preparingEnlistment)
			{
				preparingEnlistment.Done();
			}

			public void Rollback(Enlistment enlistment)
			{
				handler(this, EventArgs.Empty);
				enlistment.Done();
			}
		}
		#endregion

		/// <summary>
		/// Starts a new Amnesia session with a remote application
		/// </summary>
		public Session(Uri appUrl)
			: this(appUrl, null)
		{
		}

		/// <summary>
		/// Starts a new Amnesia session with a remote application
		/// </summary>
		public Session(string appUrl)
			: this(appUrl, null)
		{
		}

		/// <summary>
		/// Starts a new Amnesia session with a remote application
		/// </summary>
		public Session(Uri appUrl, ILog log)
			: this(appUrl.ToString(), log)
		{
		}

		/// <summary>
		/// Starts a new Amnesia session with a remote application
		/// </summary>
		public Session(string appUrl, ILog log)
		{
			this.log = log ?? NullLog.Instance;

			this.serviceUrl = appUrl + Amnesia.Settings.Current.HandlerPath;

			// Start a new distributed transaction
			TxScope = new TransactionScope(TransactionScopeOption.Required, TransactionTimeout);
			var request = new Handler.StartSessionRequest();
			Transaction tx = Transaction.Current;
			request.Transaction = tx;

			var response = request.Send(serviceUrl);
			LogResponse(response);

			response.ReceivedByClient();

			// Monitor the distributed transaction in order to detect if its aborted remotely.
			tx.EnlistVolatile(new AbortNotification((o, e) =>
			{
				if (isDisposed)
					return;

				if (request.Transaction.TransactionInformation.Status == TransactionStatus.Aborted)
				{
					wasAbortedAsync = true;

					if(onAbortedAsync != null)
						onAbortedAsync(this, EventArgs.Empty);
				}
			}),
			EnlistmentOptions.None);
		}

		void LogResponse(Handler.Response response)
		{
			foreach (string entry in response.Log.Entries)
				log.Write(entry);
		}

		/// <summary>
		/// Gets the callstack at the time a server-origin rollback occured
		/// </summary>
		public string GetServerRollbackStackTrace()
		{
			try
			{
				var status = (new Handler.GetStatusRequest().Send(serviceUrl, new TimeSpan(0, 0, 20)));
				return status.LastServerRollbackStackTrace;
			}
			catch
			{
				return null;
			}
		}

		/// <summary>
		/// Indicates if the current thread is associated with a session
		/// </summary>
		public static bool IsActive
		{
			get { return ID != Guid.Empty; }
		}

		/// <summary>
		/// Uniquely identifies the current active session
		/// </summary>
		internal static Guid ID
		{
			get
			{ 
				return id;
			} 
			set 
			{
				if (!(id == Guid.Empty || value == Guid.Empty))
					throw new InvalidOperationException("Cannot start a new session until the current one has been ended. Active=" + id + ", New=" + value);

				id = value;
			}
		}

		/// <summary>
		/// The active transaction associated with the session
		/// </summary>
		internal static Transaction Transaction { get; set; }

		/// <summary>
		/// Raised just after a session is ended
		/// </summary>
		public static event EventHandler AfterSessionEnded
		{
			add { afterSessionEnded += value; }
			remove { afterSessionEnded -= value; }
		}

		/// <summary>
		/// Raised when the session is ended.  This event will be raised by
		/// a thread other than the one that created the session and can be 
		/// raised at anytime without warning.
		/// </summary>
		public event EventHandler AbortedAsync
		{
			add { onAbortedAsync += value; }
			remove { onAbortedAsync -= value; }
		}

		/// <summary>
		/// True if the session has been aborted by a remote participate in the distributed transaction
		/// </summary>
		public bool WasAbortedAsync
		{
			get { return wasAbortedAsync; }
		}

		/// <summary>
		/// Called by a single thread when the session has ended
		/// </summary>
		internal static void RaiseAfterSessionEnded()
		{
			if (afterSessionEnded != null)
				afterSessionEnded(null, EventArgs.Empty);
		}

		/// <summary>
		/// Raised after the session is disposed
		/// </summary>
		public event EventHandler Disposed
		{
			add { onDisposed += value; }
			remove { onDisposed -= value; }
		}

		public void Dispose()
		{
			isDisposed = true;
			Session.ID = Guid.Empty;

			// transaction is completing due to local code so disable the AbortedAsync event
			onAbortedAsync = null;

			// Notify the server of the end of the session. This will rollback the transaction server-side.
			var response = (new Handler.EndSessionRequest()).Send(serviceUrl);
			LogResponse(response);

			// Now that server is aware, tear down the local transaction scope.
			Transaction.Current.Rollback();
			TxScope.Dispose();

			// raise Disposed event
			if (onDisposed != null)
				onDisposed(this, EventArgs.Empty);
		}

		/// <summary>
		/// Gets or creates a connection that can be used with the current session. If there is no active session, null is returned.
		/// This connection can and should be shared across threads for transaction rollbacks to work properly.
		/// </summary>
		public static IDbConnection GetConnection(string connectionString, Func<string, DbConnection> createConnection)
		{
			// Prevent rouge threads from access the shared connection
			Session.Tracker.AssertActivityStarted();

			string key;

			if (Transaction.TransactionInformation.DistributedIdentifier != null)
				key = Transaction.TransactionInformation.DistributedIdentifier.ToString();
			else
				key = Transaction.TransactionInformation.LocalIdentifier;

			key += "|" + connectionString;

			AmnesiaDbConnection connection = null;
			lock (connections)
			{
				if (!connections.TryGetValue(key, out connection))
				{
					connection = new AmnesiaDbConnection(Transaction, createConnection(connectionString));
					connections[key] = connection;
				}
			}

			return connection;
		}

		/// <summary>
		/// Because session connections can be shared across threads
		/// access to them must be synchronized.  For normal connections that are not part of a distributed
		/// transaction no locking is performed for better performance.
		/// </summary>
		public static IDisposable LockConnection(IDbConnection connection)
		{
			if(connections.Count == 0)
				return UndoableAction.Null;

			lock (connections)
			{
				if (!(connection is AmnesiaDbConnection) || !connections.Values.Contains((AmnesiaDbConnection)connection))
					return UndoableAction.Null;
			}

			Monitor.Enter(connection);

			return new UndoableAction(delegate
			{
				Monitor.Exit(connection);
			});
		}

		/// <summary>
		/// Closes all database connections associated with the session.
		/// </summary>
		internal static void CloseConnections(ILog log)
		{
			List<AmnesiaDbConnection> oldConnections = new List<AmnesiaDbConnection>();

			lock (connections)
			{
				foreach (AmnesiaDbConnection c in connections.Values)
					oldConnections.Add(c);

				connections.Clear();
			}

			foreach (AmnesiaDbConnection connection in oldConnections)
			{
				try
				{
					using (LockConnection(connection))
					{
						if (connection.Real.State == ConnectionState.Open)
						{
							log.Write("Closing: " + connection.Real.ConnectionString);
							connection.Real.Close();
						}
					}
				}
				catch { }
			}
		}

		/// <summary>
		/// Registers an activity with the session. This method only needs to be called on threads that are
		/// not tied to an HttpContext.
		/// </summary>
		/// <returns></returns>
		public static IAsyncActivity NewAsyncActivity()
		{
			return Session.IsActive ? new AsyncActivity(Tracker) : NullAsyncActivity.Instance;
		}
	}
}
