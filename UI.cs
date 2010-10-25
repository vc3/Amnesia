using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;
using System.Transactions;
using System.Threading;

namespace Amnesia
{
	class UI
	{
		static TransactionScope rootScope;
		static Transaction rootTransaction;
		static Thread scopeThread;
		static Mutex sessionEnded;
		static ManualResetEvent requestThreadProceed = new ManualResetEvent(false);
		static ManualResetEvent scopeThreadProceed = new ManualResetEvent(false);

		public static void ProcessRequest(HttpContext ctx)
		{
			if (ctx.Request.QueryString.ToString() == "start")
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
				(new Handler.StartSessionRequest() { Transaction = rootTransaction }).Execute();
			}
			else if (ctx.Request.QueryString.ToString() == "end")
			{
				try
				{
					(new Handler.EndSessionRequest()).Execute();
				}
				finally
				{
					// notify the root thread that the scope can be released
					requestThreadProceed.Reset();
					scopeThreadProceed.Set();
					requestThreadProceed.WaitOne();
					scopeThread = null;
					sessionEnded = null;
				}
			}

			// Output UI based on new state
			ctx.Response.Write(@"
					<html>
					<body>");

			if(Module.Transaction == null)
				ctx.Response.Write(@"<a href='?start'>Start Session</a>");
			else
				ctx.Response.Write(string.Format(@"
					Session began at {0}: <a href='?end'>End Session</a>",
					Module.Transaction.TransactionInformation.CreationTime));

			ctx.Response.Write(@"<br /><br /><a href='?status'>Refresh Status</a>");

			ctx.Response.Write(@"
				</body>
				</html>");
		}
	}
}
