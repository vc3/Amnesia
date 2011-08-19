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
	public class Handler : IHttpAsyncHandler
	{
		const int WebServerLockTimeoutMS = 60000;

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

		/// <summary>
		/// Stack trace of the last server-origin rollback
		/// </summary>
		static string lastServerRollbackStackTrace;

		#region IHttpAsyncHandler
		IAsyncResult IHttpAsyncHandler.BeginProcessRequest(HttpContext ctx, AsyncCallback callback, object extraData)
		{
			try
			{
				if (ctx.Request.Url.GetComponents(UriComponents.Path, UriFormat.Unescaped).ToLower().EndsWith("/ui"))
				{
					UI.ProcessRequest(ctx);
					return SyncResult.Done;
				}
				else
				{
					string reqPayload;

					using (StreamReader reader = new StreamReader(ctx.Request.InputStream))
						reqPayload = reader.ReadToEnd();

					ICommand command = (ICommand)SerializationUtil.DeserializeBase64(reqPayload);
					return command.BeginExecute(ctx, callback);
				}
			}
			catch (Exception error)
			{
				var errorResponse = new ErrorResponse() { Message = error.Message, ExceptionType = error.GetType().FullName, StackTrace = error.StackTrace };
				ctx.Response.Write(SerializationUtil.SerializeBase64(errorResponse));
				return SyncResult.Done;
			}
		}

		void IHttpAsyncHandler.EndProcessRequest(IAsyncResult result)
		{
		}

		bool IHttpHandler.IsReusable
		{
			get { return true; }
		}

		void IHttpHandler.ProcessRequest(HttpContext context)
		{
			throw new InvalidOperationException();
		}
		#endregion


		/// <summary>
		/// Calls must handle concurrency control
		/// </summary>
		static void Rollback(ILog log)
		{
			ThreadUtil.StopThreadPoolKeepAlive();

			// Dispose of transaction object on all threads
			ThreadUtil.ForAllThreads(delegate
			{
				if (TransactionScope != null)
				{
					try
					{
						TransactionScope.Dispose();
						log.Write("  Transaction started on thread " + Thread.CurrentThread.ManagedThreadId);
					}
					catch(Exception e)
					{
						log.Write("  Failed to rollback transaction on thread " + Thread.CurrentThread.ManagedThreadId + ": " + e.Message);
						throw;
					}
					finally
					{
						TransactionScope = null;
					}
				}
			}, "rollback");

			// the session has ended
			Session.ID = Guid.Empty;
		}

		#region Response
		[Serializable]
		public class Response : IAsyncResult
		{
			[NonSerialized]
			bool isCompleted = false;

			internal SerializableLog Log { get; private set; }

			protected Response()
			{
				Log = new SerializableLog();
			}

			internal void Completed()
			{
				isCompleted = true;
			}

			/// <summary>
			/// Called after response is deserialized on the client
			/// </summary>
			internal virtual void ReceivedByClient()
			{
			}

			#region IAsyncResult
			object IAsyncResult.AsyncState
			{
				get { return null; }
			}

			WaitHandle IAsyncResult.AsyncWaitHandle
			{
				get { return null; }
			}

			bool IAsyncResult.CompletedSynchronously
			{
				get { return false; }
			}

			bool IAsyncResult.IsCompleted
			{
				get { return isCompleted; }
			}
			#endregion
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

			internal override void Execute(HttpContext ctx)
			{
				// Wait for all currently executing ASP requests to complete so we're in a clean state
				// before messing around with the thread pool and transactions.  Being extra careful here
				// should also prevent bleed over from any prior sessions into this one.
				using (Module.LockWebServer(WebServerLockTimeoutMS, true, Response.Log))
				{
					// If there is currently an open session, end it before starting a new one
					if (Session.IsActive)
					{
						Response.Log.Write("Ending prior session ({0})", Session.ID);
						EndSessionRequest.EndSession(Response.Log);
						Response.Log.Write("> prior session ended");
					}

					lastServerRollbackStackTrace = null;

					// Watch for when the transaction ends unexpectedly so some cleanup can occur.
					// This event handler will run on the thread that is causing the rollback which is likely a
					// different thread than is registering the event handler.
					// Do this before flooding the thread pool so a failure while propogating the transaction is cleaned up.
					Transaction.TransactionCompleted += Transaction_TransactionCompleted;

					// Propagate the transaction to all threads in the thread pool.
					// Must do this proactively rather than in a module due to thread switches
					// that may occur during I/O operations.
					sessionId = Session.ID = Guid.NewGuid();
					
					ThreadUtil.StartThreadPoolKeepAlive();

					Response.Log.Write("Starting session ({0})", sessionId);
					ThreadUtil.ForAllThreads(delegate
					{
						TransactionScope = new TransactionScope(Transaction.DependentClone(DependentCloneOption.RollbackIfNotComplete));
						Response.Log.Write("  Transaction started on thread " + Thread.CurrentThread.ManagedThreadId);
					}, "start transaction");
					Response.Log.Write("> session started");
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

				Response.Log.Write("Transaction aborted unexpectedly by server!  An async rollback will begin momentarily.");

				// Queue the rollback operation on a different, non-thread pool thread
				// so that the web server can be paused before cleaning up.
				Thread rollbackThread = new Thread(delegate()
				{
					Response.Log.Write("Async rollback thread is now running");
					using (Module.LockWebServer(WebServerLockTimeoutMS, false, Response.Log))
					{
						// Verify the session is still active. It may have been ended before the rollback thread was able to
						// obtain a lock.  Be sure to only end a session that was started by this particular request (via sessionId).
						if (Session.ID == sessionId)
						{
							Response.Log.Write("Rolling back session ({0})", Session.ID);
							Rollback(Response.Log);
							Response.Log.Write("> rollback completed");
						}
						else
							Response.Log.Write("There is no active session to rollback");
					}
					Response.Log.Write("> Async rollback thread is exiting");
				});

				rollbackThread.Start();
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
			internal override void Execute(HttpContext ctx)
			{
				using (Module.LockWebServer(WebServerLockTimeoutMS, true, Response.Log))
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
				// End the transaction for all threads
				// Wait for all threads to rollback before proceeding so
				// everything is tidy when the request is completed.
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
			internal override void Execute(HttpContext ctx)
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

		#region SyncResult
		class SyncResult : IAsyncResult
		{
			public static readonly IAsyncResult Done = new SyncResult();

			private SyncResult()
			{
			}

			public object AsyncState
			{
				get { return null; }
			}

			public WaitHandle AsyncWaitHandle
			{
				get { return null; }
			}

			public bool CompletedSynchronously
			{
				get { return true; }
			}

			public bool IsCompleted
			{
				get { return true; }
			}
		}

		#endregion
	}
}
