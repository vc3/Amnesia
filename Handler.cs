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
			// mark the session as ended before doing any rollback work
			Session.ID = Guid.Empty;
			Session.IsRollbackPending = false;
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
			internal SerializableLog AsyncLog { get; private set; }
			internal SerializableLog Log { get; private set; }

			protected Response()
			{
				Log = new SerializableLog();
				AsyncLog = new SerializableLog();
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
				using (Session.Tracker.Exclusive(WebServerLockTimeoutMS, Response.Log))
				{
					Session.AsyncLog.CopyInto(Response.AsyncLog);
					Session.AsyncLog = new SerializableLog();

					// If there is currently an open session, end it before starting a new one
					if (Session.IsActive)
					{
						Response.Log.Write("Ending prior session ({0})", Session.ID);
						EndSessionRequest.EndSession(Response.Log);
					}

					sessionId = Session.ID = Guid.NewGuid();
					Session.Transaction = Transaction;
					Response.Log.Write("Session started ({0})", sessionId);
					Session.AsyncLog = new SerializableLog();

					Module.PersistSessionState();


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

				using (Session.Tracker.Exclusive(10000, null))
				{
					if (sessionId != Session.ID)
						return;

					StackTrace stackTrace = new StackTrace(1, true);
					Session.AsyncLog.Write("Transaction aborted unexpectedly by server! Session: {0} \n{1}", sessionId, stackTrace);

					// Stop all app requests until the session is rolled back
					Session.IsRollbackPending = true;
				}

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
				using (Session.Tracker.Exclusive(WebServerLockTimeoutMS, Response.Log))
				{
					Session.AsyncLog.CopyInto(Response.AsyncLog);
					Session.AsyncLog = new SerializableLog();

					if (Session.IsActive)
					{
						Response.Log.Write("Ending the active session ({0})", Session.ID);
						EndSession(Response.Log);
						Response.Log.Write("Session has been ended");
					}
					else if(Session.IsRollbackPending)
					{
						// handle unexpected app domain restarts where there's no active session but a rollback is still expected
						Session.IsRollbackPending = false;
						Response.Log.Write("Cleaned up session after unexpected app domain restart");
					}
					else
					{
						Response.Log.Write("There is no active session to end");
					}

					Module.PersistSessionState();
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
	}
}
