using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Transactions;

namespace Amnesia
{
	public class Session : IDisposable
	{
		static TimeSpan TransactionTimeout = TimeSpan.Zero;

		public string ID;

		internal TransactionScope TxScope;
		string serviceUrl;

		public Session(Uri appUrl)
			: this(appUrl.ToString())
		{
		}

		public Session(string appUrl)
		{
			this.serviceUrl = appUrl + "/Amnesia.axd";

			// Start a new distributed transaction
			TxScope = new TransactionScope(TransactionScopeOption.Required, TransactionTimeout);
			var request = new Handler.StartSessionRequest();
			request.Transaction = Transaction.Current;
			
			var response = request.Send(serviceUrl);
			ID = response.SessionID;
		}

		public void Dispose()
		{
			// Tear down the local transaction scope.
			Transaction.Current.Rollback();			
			TxScope.Dispose();

			// Notify the server of the end of the session
			(new Handler.EndSessionRequest() { SessionID = ID }).Send(serviceUrl);
		}
	}
}
