using System;
using System.Web;
using System.Transactions;
using System.Collections.Generic;

namespace Amnesia
{
	public class Module : IHttpModule
	{
		static internal Transaction Transaction;

		static object itemsKey = new object();
		
		public void Dispose()
		{
		}

		bool IsHandlerRequest
		{
			get
			{
				return HttpContext.Current.Request.RawUrl.ToLower().Contains(Settings.Current.HandlerPath.ToLower());
			}
		}

		public void Init(HttpApplication context)
		{
			context.BeginRequest += delegate
			{
				if (IsHandlerRequest)
					return;

				if (Transaction != null)
				{
					// join the distributed transaction prior to request
					TransactionScope scope = new TransactionScope(Transaction.DependentClone(DependentCloneOption.BlockCommitUntilComplete), TimeSpan.Zero);
					HttpContext.Current.Items[itemsKey] = scope;
				}
			};

			context.EndRequest += delegate
			{
				TransactionScope scope = (TransactionScope)HttpContext.Current.Items[itemsKey];

				if (scope != null)
				{
					// commit this part of the work so entire transaction doesn't rollback
					scope.Complete();

					// release scope
					scope.Dispose();
				}
			};
		}
	}
}
