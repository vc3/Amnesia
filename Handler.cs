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

		public bool IsReusable
		{
			get { return true; }
		}

		public void ProcessRequest(HttpContext ctx)
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
				// End prior session if needed
				if (Session.IsActive)
					(new EndSessionRequest()).Execute();


				// Propagate the transaction to all threads in the thread pool.
				// Must to the proactively rather than in a module to to thread switches
				// that may occur during I/O operations.

				ThreadUtil.StartThreadPoolKeepAlive();

				ThreadUtil.ForAllThreads(delegate
				{
					// TODO: need to synchronize access to transaction?
					TransactionScope = new TransactionScope(Transaction.DependentClone(DependentCloneOption.RollbackIfNotComplete));
				});

				Session.IsActive = true;


				// Watch for when transaction ends unexpectedly so some cleanup can occur
				Transaction.TransactionCompleted += delegate
				{
					(new EndSessionRequest()).Execute();
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
		/// Request that can be sent to the handler to end an active session
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
					ThreadUtil.ForAllThreads(delegate
					{
						TransactionScope.Dispose();
						TransactionScope = null;
					});

					ThreadUtil.StopThreadPoolKeepAlive();

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
