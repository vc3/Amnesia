using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;
using System.Transactions;
using System.Threading;
using System.IO;

namespace Amnesia
{
	class UI
	{
		static TransactionScope rootScope;
		static Transaction rootTransaction;
		static Thread scopeThread;
		static ManualResetEvent requestThreadProceed = new ManualResetEvent(false);
		static ManualResetEvent scopeThreadProceed = new ManualResetEvent(false);

		[ThreadStatic]
		static TransactionScope currentScope;

		public static void ProcessRequest(HttpContext ctx)
		{
			bool silent = !string.IsNullOrEmpty(ctx.Request.QueryString["silent"]);

			if (ctx.Request.QueryString["cmd"] == "start")
			{
				if (scopeThread != null)
				    throw new InvalidOperationException("Session is already started");

				scopeThread = new Thread(arg =>
				{
				    // create new scope
				    using (rootScope = new TransactionScope(TransactionScopeOption.Required, TimeSpan.Zero))
				    {
				        rootTransaction = Transaction.Current;

				        // notify request thread that scope is created
				        requestThreadProceed.Set();

				        // wait for session to end
				        scopeThreadProceed.WaitOne();
				    }

				    // return control to request thread
				    requestThreadProceed.Set();
				});


				// wait for the the helper thread create the root scope
				scopeThreadProceed.Reset();
				requestThreadProceed.Reset();
				
				scopeThread.Start();

				requestThreadProceed.WaitOne();

				// use the transaction created by the scopeThread
				(new Handler.StartSessionRequest() { Transaction = rootTransaction }).Execute(HttpContext.Current);
			}
			else if (ctx.Request.QueryString["cmd"] == "end")
			{
				try
				{
					(new Handler.EndSessionRequest()).Execute(HttpContext.Current);
				}
				finally
				{
					// notify the root thread that the scope can be released
					requestThreadProceed.Reset();
					scopeThreadProceed.Set();
					requestThreadProceed.WaitOne();
					scopeThread = null;
				}
			}

			if (!silent)
			{
				// Output UI based on new state
				ctx.Response.Write(@"
					<html>
					<body>");

				if (!Session.IsActive)
					ctx.Response.Write(@"<a href='?cmd=start'>Start Session</a>");
				else
				{
					ctx.Response.Write(string.Format(@"<a href='?cmd=end'>End Session</a><br/>"));
					ctx.Response.Write(string.Format(@"ID: {0}</br>", Session.ID));
					ctx.Response.Write(string.Format(@"Transaction: {0}</br>", Session.Transaction.TransactionInformation.Status));
				}
				ctx.Response.Write(@"<br /><br /><a href='?cmd=status'>Refresh Status</a>");

				ctx.Response.Write(@"
				</body>
				</html>");
			}
		}
	}
}
