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
				{
					(new EndSessionRequest()).Execute();
				}

				Module.Transaction = Transaction;
				Module.rootThread = System.Threading.Thread.CurrentThread.ManagedThreadId;

				Transaction.TransactionCompleted += delegate
				{
					Session.End();
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
				Session.End();

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
