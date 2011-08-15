using System;
using System.Web;
using System.IO;
using System.Transactions;
using System.Web.Configuration;
using System.Threading;

namespace Amnesia
{
	public class Handler : IHttpHandler
	{
		/// <summary>
		/// the processing of a single request can change threads if
		/// asynchronous I/O occurs during that request.  In order to guarantee
		/// all requests participate in the transaction the handler will propagate
		/// the transaction to all threads in the thread pool.
		/// 
		/// The scope for each thread is tracked in this variable.
		/// </summary>
		[ThreadStatic]
		static internal TransactionScope TransactionScope;

		static private bool rollbackStarted;

		public bool IsReusable
		{
			get { return true; }
		}

		public void ProcessRequest(HttpContext ctx)
		{
			try
			{
				if (ctx.Request.Url.GetComponents(UriComponents.Path, UriFormat.Unescaped).ToLower().EndsWith("/ui"))
					UI.ProcessRequest(ctx);
				else
				{
					string reqPayload;

					using (StreamReader reader = new StreamReader(ctx.Request.InputStream))
						reqPayload = reader.ReadToEnd();

					ICommand command = (ICommand)SerializationUtil.DeserializeBase64(reqPayload);
					object response = command.Execute();
					ctx.Response.Write(SerializationUtil.SerializeBase64(response));
				}
			}
			catch (Exception error)
			{
				var errorResponse = new ErrorResponse() { Message = error.Message, ExceptionType = error.GetType().FullName, StackTrace = error.StackTrace };
				ctx.Response.Write(SerializationUtil.SerializeBase64(errorResponse));
			}
		}

		static void Rollback(bool async)
		{
			if (!rollbackStarted)
			{
				rollbackStarted = true;
				ThreadUtil.StopThreadPoolKeepAlive();

				ThreadUtil.ForAllThreads(async, delegate
				{
					if (TransactionScope != null)
					{
						try
						{
							TransactionScope.Dispose();
						}
						finally
						{
							TransactionScope = null;
						}
					}
				}, "rollback, async=" + async);
			}
		}

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

			internal override StartSessionResponse Execute()
			{
				if (Session.IsActive)
					(new EndSessionRequest()).Execute();

				// Propagate the transaction to all threads in the thread pool.
				// Must to the proactively rather than in a module to to thread switches
				// that may occur during I/O operations.
				ThreadUtil.StartThreadPoolKeepAlive();

				ThreadUtil.ForAllThreads(false, delegate
				{
					TransactionScope = new TransactionScope(Transaction.DependentClone(DependentCloneOption.RollbackIfNotComplete));
				}, "start transaction");

				Session.IsActive = true;
				rollbackStarted = false;

				// Watch for when transaction ends unexpectedly so some cleanup can occur
				Transaction.TransactionCompleted += delegate {
					// The rollback on the other threads depend on this one returning from
					// this event handler so must do the rollback asynchronously, otherwise
					// there will be a deadlock.
					Rollback(true);
				};

				return new StartSessionResponse();
			}
		}

		[Serializable]
		internal class StartSessionResponse
		{
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
			internal override EndSessionResponse Execute()
			{
				if (Session.IsActive)
				{
					Session.IsActive = false;

					// End the transaction for all threads
					// Wait for all threads to rollback before proceeding so
					// everything is tidy when the request is completed.
					Rollback(false);

					// Raise event to notify application session has completed
					Session.RaiseAfterSessionEnded();
				}

				return new EndSessionResponse();
			}
		}

		[Serializable]
		internal class EndSessionResponse
		{
		}
		#endregion
	}
}
