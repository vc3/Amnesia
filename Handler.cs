﻿using System;
using System.Web;
using System.IO;
using System.Transactions;
using System.Web.Configuration;
using System.Threading;
using System.Collections;
using System.Text;

namespace Amnesia
{
	public class Handler : IHttpHandler
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

		/// <summary>
		/// Calls must handle concurrency control
		/// </summary>
		static void Rollback()
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
					}
					finally
					{
						TransactionScope = null;
					}
				}
			}, "rollback");

			Session.IsActive = false;
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
				// Wait for all currently executing ASP requests to complete so we're in a clean state
				// before messing around with the thread pool and transactions.  Being extra careful here
				// should also prevent bleed over from any prior sessions into this one.
				using (Module.LockWebServer(WebServerLockTimeoutMS))
				{
					// If there is currently an open session, end it before starting a new one
					if (Session.IsActive)
						EndSessionRequest.EndSession();

					lastServerRollbackStackTrace = null;

					// Watch for when the transaction ends unexpectedly so some cleanup can occur.
					// This event handler will run on the thread that is causing the rollback which is likely a
					// different thread than is registering the event handler.
					// Do this before flooding the thread pool so a failure while propogating the transaction is cleaned up.
					Transaction.TransactionCompleted += Transaction_TransactionCompleted;

					// Propagate the transaction to all threads in the thread pool.
					// Must do this proactively rather than in a module to to thread switches
					// that may occur during I/O operations.
					Session.IsActive = true;

					ThreadUtil.StartThreadPoolKeepAlive();

					ThreadUtil.ForAllThreads(delegate
					{
						TransactionScope = new TransactionScope(Transaction.DependentClone(DependentCloneOption.RollbackIfNotComplete));
					}, "start transaction");

					return new StartSessionResponse();
				}
			}

			void Transaction_TransactionCompleted(object sender, TransactionEventArgs e)
			{
				// Queue the rollback operation on a different, non-thread pool thread
				// so that all the web server can be paused before cleaning up.
				Thread rollbackThread = new Thread(delegate()
				{
					if (Session.IsActive)
						using (Module.LockWebServer(WebServerLockTimeoutMS))
						{
							lastServerRollbackStackTrace = GetFullStackTrace();

							if (Session.IsActive)
								Rollback();
						}
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
				using (Module.LockWebServer(WebServerLockTimeoutMS))
				{
					if (Session.IsActive)
						EndSession();
				}

				return new EndSessionResponse();
			}

			/// <summary>
			/// Callers must handle concurrency control
			/// </summary>
			internal static void EndSession()
			{
				// End the transaction for all threads
				// Wait for all threads to rollback before proceeding so
				// everything is tidy when the request is completed.
				Rollback();

				// Raise event to notify application session has completed
				Session.RaiseAfterSessionEnded();
			}
		}

		[Serializable]
		internal class EndSessionResponse
		{
		}
		#endregion

		#region GetStatus
		/// <summary>
		/// Gets status information about Amnesia
		/// </summary>
		[Serializable]
		internal class GetStatusRequest : Command<GetStatusResponse>
		{
			internal override GetStatusResponse Execute()
			{
				return new GetStatusResponse()
				{
					LastServerRollbackStackTrace = Handler.lastServerRollbackStackTrace
				};
			}

			/// <summary>
			/// Callers must handle concurrency control
			/// </summary>
			internal static void EndSession()
			{
				// End the transaction for all threads
				// Wait for all threads to rollback before proceeding so
				// everything is tidy when the request is completed.
				Rollback();

				// Raise event to notify application session has completed
				Session.RaiseAfterSessionEnded();
			}
		}

		[Serializable]
		internal class GetStatusResponse
		{
			public string LastServerRollbackStackTrace;
		}
		#endregion
	}
}
