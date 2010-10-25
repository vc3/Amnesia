using System;
using System.Web;
using System.Transactions;
using System.Collections.Generic;

namespace Amnesia
{
	public class Module : IHttpModule
	{
		static internal Transaction Transaction;
		internal static int rootThread;
		static List<int> threads = new List<int>();

		static object itemsKey = new object();

		public void Dispose()
		{
		}

		public void Init(HttpApplication context)
		{
			context.BeginRequest += delegate
			{
				if (Transaction != null)
				{
					lock(threads)
					{
						if(!threads.Contains(System.Threading.Thread.CurrentThread.ManagedThreadId))
							threads.Add(System.Threading.Thread.CurrentThread.ManagedThreadId);
					}

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
