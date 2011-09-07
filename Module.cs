using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;
using System.Threading;
using System.Transactions;
using System.IO;

namespace Amnesia
{
	public class Module : IHttpModule
	{
		static object REQUEST_ABORTED = new object();

		static bool? moduleRegistered;
		static AutoResetEvent requestsDone;

		static bool sessionRestored;
		static object stateFileMutex = new object();
		static string stateFile;

		void IHttpModule.Dispose()
		{
		}

		void IHttpModule.Init(HttpApplication context)
		{
			context.BeginRequest += context_BeginRequest;
			context.EndRequest += context_EndRequest;
		}

		/// <summary>
		/// Tracks information about the session so rollbacks can be handled cleanly across app domain restarts
		/// </summary>
		static internal void PersistSessionState()
		{
			if (Session.ID == Guid.Empty)
			{
				if (File.Exists(stateFile))
					File.Delete(stateFile);
			}
			else
			{
				File.WriteAllText(stateFile, Session.ID.ToString());
			}
		}

		/// <summary>
		/// Reads information about the session so rollbacks can be handled cleanly across app domain restarts
		/// </summary>
		static private void RestoreSessionState()
		{
			if (File.Exists(stateFile))
			{
				Session.IsRollbackPending = true;
			}
		}

		void context_BeginRequest(object sender, EventArgs e)
		{
			try
			{
				// Restore session state if this is the first request
				if (!sessionRestored)
				{
					lock (stateFileMutex)
					{
						if (!sessionRestored)
						{
							stateFile = Path.Combine(HttpContext.Current.Server.MapPath("~/"), Settings.Current.StateFile);
							RestoreSessionState();
							sessionRestored = true;
						}
					}
				}

				// make sure application requests do not get through if an unexpected transaction abort occurs
				if (Session.IsRollbackPending && !(HttpContext.Current.Request.Url.ToString().ToLower().Contains(Settings.Current.HandlerPath.ToLower()) ))
					throw new ApplicationException("Transaction was aborted and is awaiting rollback");

				Session.Tracker.StartActivity();
			}
			catch(Exception err)
			{
				HttpContext.Current.Items[REQUEST_ABORTED] = true;

				HttpContext.Current.Response.StatusCode = 503; // service unavailable
				HttpContext.Current.Response.Write("Amnesia is currently blocking requests. Try again in a few moments. " + err.Message);
				HttpContext.Current.Response.End();
				return;
			}
		}

		void context_EndRequest(object sender, EventArgs e)
		{
			if (HttpContext.Current.Items[REQUEST_ABORTED] == null)
				Session.Tracker.EndActivity();
		}

		internal static void EnsureRegistered()
		{
			if (!IsEnabled)
				throw new InvalidOperationException(@"Amnesia's HTTP Module must be registered. Here's some XML that may help: <add name=""Amnesia"" type=""Amnesia.Module, Amnesia"" />");
		}

		/// <summary>
		/// True if Amnesia is enabled for the web application
		/// </summary>
		public static bool IsEnabled
		{
			get
			{
				if (!moduleRegistered.HasValue)
				{
					if (HttpContext.Current != null)
					{
						foreach (string moduleName in HttpContext.Current.ApplicationInstance.Modules)
						{
							if (HttpContext.Current.ApplicationInstance.Modules[moduleName] is Amnesia.Module)
							{
								moduleRegistered = true;
								break;
							}
						}
					}
					if (!moduleRegistered.HasValue)
						moduleRegistered = false;
				}

				return moduleRegistered.Value;
			}
		}
	}
}
