using System;
using System.Web;
using System.IO;
using System.Transactions;

namespace Amnesia
{
	public class Handler : IHttpHandler
	{
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
		[Serializable]
		internal class StartSessionRequest : Command<StartSessionResponse>
		{
			public Transaction Transaction;

			internal override StartSessionResponse Execute()
			{
				StartSessionResponse response = new StartSessionResponse() {
					SessionID = Guid.NewGuid().ToString()
				};

				if (Module.Transaction != null)
					throw new InvalidOperationException("Must end prior session before starting a new one");

				Module.Transaction = Transaction;
				Module.rootThread = System.Threading.Thread.CurrentThread.ManagedThreadId;

				Transaction.TransactionCompleted += delegate
				{
					Module.Transaction = null;
				};

				//Module.Sessions.Add(response.SessionID, Transaction);

				return response;
			}
		}

		[Serializable]
		internal class StartSessionResponse
		{
			public string SessionID;
		}
		#endregion

		#region EndSession
		[Serializable]
		internal class EndSessionRequest : Command<EndSessionResponse>
		{
			public string SessionID;

			internal override EndSessionResponse Execute()
			{
				Module.Transaction = null;

				//lock (Module.Sessions)
				//{
				//    Module.Sessions.Remove(SessionID);
				//}

				//Transaction transaction;
				//if (Module.Sessions.TryGetValue(SessionID, out transaction))
				//{
				//}

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
