using System;
using System.Web;
using System.IO;
using System.Transactions;
using System.Web.Configuration;
using System.Threading;
using System.Collections;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;

namespace Amnesia
{
	public class Handler : IHttpHandler
	{
		const int WebServerLockTimeoutMS = 60000;

		/// <summary>
		/// Stack trace of the last server-origin rollback
		/// </summary>
		static string lastServerRollbackStackTrace;

		#region IHttpAsyncHandler

		bool IHttpHandler.IsReusable
		{
			get { return true; }
		}

		void IHttpHandler.ProcessRequest(HttpContext ctx)
		{
			try
			{
				if (ctx.Request.Url.GetComponents(UriComponents.Path, UriFormat.Unescaped).ToLower().EndsWith("/ui"))
				{
					UI.ProcessRequest(ctx);
				}
				else
				{
					string reqPayload;

					using (StreamReader reader = new StreamReader(ctx.Request.InputStream))
						reqPayload = reader.ReadToEnd();

					ICommand command = (ICommand)SerializationUtil.DeserializeBase64(reqPayload);
					Response response = command.Execute(ctx);
					ctx.Response.Write(SerializationUtil.SerializeBase64(response));
				}
			}
			catch (Exception error)
			{
				var errorResponse = new ErrorResponse() { Message = error.Message, ExceptionType = error.GetType().FullName, StackTrace = error.StackTrace };
				ctx.Response.Write(SerializationUtil.SerializeBase64(errorResponse));
			}
		}
		#endregion


		/// <summary>
		/// Calls must handle concurrency control
		/// </summary>
		static void Rollback(ILog log)
		{
			// the session has ended
			Session.ID = Guid.Empty;

			Session.CloseConnections(log);

			log.Write("Aborting transaction");
			Session.Transaction.Rollback();
			Session.Transaction.Dispose();
			Session.Transaction = null;
		}

		#region Response
		[Serializable]
		public class Response
		{
			internal SerializableLog Log { get; private set; }

			protected Response()
			{
				Log = new SerializableLog();
			}

			/// <summary>
			/// Called after response is deserialized on the client
			/// </summary>
			internal virtual void ReceivedByClient()
			{
			}
		}
		#endregion

		#region ErrorResponse
		[Serializable]
		internal class ErrorResponse
		{
			public string Message { get; set; }
			public string ExceptionType { get; set; }
			public string StackTrace { get; set; }
		}
		#endregion

		#region StartSession
		/// <summary>
		/// Request that can be sent to the handler to start a new session
		/// </summary>
		[Serializable]
		internal class StartSessionRequest : Command<StartSessionResponse>
		{
			/// <summary>
			/// Serialize and deserializing this transaction across process boundaries
			/// makes the distributed transaction possible.
			/// </summary>
			public Transaction Transaction;

			Guid sessionId;

			public override void Execute(HttpContext ctx)
			{
				Module.EnsureRegistered();

				// Wait for all currently executing ASP requests to complete so we're in a clean state
				// before messing around with transactions.  Being extra careful here
				// should also prevent bleed over from any prior sessions into this one.
				using (Session.Tracker.Exclusive(WebServerLockTimeoutMS, true, Response.Log))
				{
					// If there is currently an open session, end it before starting a new one
					if (Session.IsActive)
					{
						Response.Log.Write("Ending prior session ({0})", Session.ID);
						EndSessionRequest.EndSession(Response.Log);
						Response.Log.Write("> prior session ended");
					}

					lastServerRollbackStackTrace = null;

					sessionId = Session.ID = Guid.NewGuid();
					Session.Transaction = Transaction;
					Response.Log.Write("Session started ({0})", sessionId);

					// Watch for when the transaction ends unexpectedly so some cleanup can occur.
					// This event handler will run on the thread that is causing the rollback which is likely a
					// different thread than is registering the event handler.
					Transaction.TransactionCompleted += Transaction_TransactionCompleted;
				}
			}

			void Transaction_TransactionCompleted(object sender, TransactionEventArgs e)
			{
				// Only attempt a rollback if the session we are interested in is still active
				if (sessionId != Session.ID)
					return;

				if (Settings.Current.DebugOnUnexpectedRollback)
				{
					if (!Debugger.IsAttached)
						Debugger.Launch();

					Debugger.Break();
				}

				lastServerRollbackStackTrace = GetFullStackTrace();

				Response.Log.Write("Transaction aborted unexpectedly by server!");

				Thread rollbackThread = new Thread(delegate()
				{
					// wait a moment to give the transaction a chance to explicitly roll back
					Thread.Sleep(5000);

					if (sessionId != Session.ID)
						return;

					using(Session.Tracker.Exclusive(10000, false, null))
					{
						if (sessionId != Session.ID)
							return;

						Rollback(NullLog.Instance);
					}
				});
			}

			/// <summary>
			/// Utility method for getting the full stack trace for a list
			/// of chained exceptions.
			/// </summary>
			/// <param name="error">Last exception in chain</param>
			/// <returns>Stack trace</returns>
			static string GetFullStackTrace()
			{
				Exception error = new Exception();

				// Include exception info in the message
				Stack errors = new Stack();
				for (Exception e = error; null != e; e = e.InnerException)
					errors.Push(e);

				StringBuilder stackTrace = new StringBuilder();
				while (errors.Count > 0)
				{
					Exception e = (Exception)errors.Pop();
					stackTrace.AppendFormat("{0}\n {1}\n{2}\n\n", e.Message, e.GetType().FullName, e.StackTrace);
				}

				return stackTrace.ToString();
			}
		}

		[Serializable]
		internal class StartSessionResponse : Response
		{
			public Guid SessionID {get; private set;}

			internal override void ReceivedByClient()
			{
				Session.ID = Guid.Empty;
				Session.ID = SessionID;
			}
		}
		#endregion

		#region EndSession
		/// <summary>
		/// Request that can be sent to the handler to end an active session.
		/// No need to explicitly end the session if a transaction is rolled back due to a failure
		/// </summary>
		[Serializable]
		internal class EndSessionRequest : Command<EndSessionResponse>
		{
			public override void Execute(HttpContext ctx)
			{
				using (Session.Tracker.Exclusive(WebServerLockTimeoutMS, true, Response.Log))
				{
					if (Session.IsActive)
					{
						Response.Log.Write("Ending the active session ({0})", Session.ID);
						EndSession(Response.Log);
						Response.Log.Write("Session has been ended");
					}
					else
					{
						Response.Log.Write("There is no active session to end");

						if (lastServerRollbackStackTrace != null)
						{
							Response.Log.Write("Stack trace from server-origin rollback: " + lastServerRollbackStackTrace);
							lastServerRollbackStackTrace = null;
						}
					}
				}
			}

			/// <summary>
			/// Callers must handle concurrency control
			/// </summary>
			internal static void EndSession(ILog log)
			{
				// End the transaction
				Rollback(log);

				// Raise event to notify application session has completed
				Session.RaiseAfterSessionEnded();
			}
		}

		[Serializable]
		internal class EndSessionResponse : Response
		{
			internal override void ReceivedByClient()
			{
				Session.ID = Guid.Empty;
			}
		}
		#endregion

		#region GetStatus
		/// <summary>
		/// Gets status information about Amnesia
		/// </summary>
		[Serializable]
		internal class GetStatusRequest : Command<GetStatusResponse>
		{
			public override void Execute(HttpContext ctx)
			{
				Response.LastServerRollbackStackTrace = Handler.lastServerRollbackStackTrace;
			}
		}

		[Serializable]
		internal class GetStatusResponse : Response
		{
			public string LastServerRollbackStackTrace;
		}
		#endregion
	}
}
