using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;
using System.Threading;
using System.Transactions;
using System.IO;
using System.Diagnostics;

namespace Amnesia
{
	public class Module : IHttpModule
	{
		static object REQUEST_ABORTED = new object();
		static object REQUEST_STARTED = new object();		

		static bool? moduleRegistered;

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
				// Make sure the parent directory is there before saving the file.
				if (!Directory.Exists(Path.GetDirectoryName(stateFile)))
					Directory.CreateDirectory(Path.GetDirectoryName(stateFile));

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
				
				// If a previous module aborts the request, begin request never runs
				HttpContext.Current.Items[REQUEST_STARTED] = true;
			}
			catch(Exception err)
			{
				HttpContext.Current.Items[REQUEST_ABORTED] = true;

				HttpContext.Current.Response.StatusCode = 503; // service unavailable
				HttpContext.Current.Response.Write("Amnesia is currently blocking requests. " + err.Message);
				HttpContext.Current.Response.End();
				return;
			}
		}

		void context_EndRequest(object sender, EventArgs e)
		{
			// ensure the request has actually begun (in cases of premature module abortion)
			if (HttpContext.Current.Items[REQUEST_ABORTED] == null &&
				HttpContext.Current.Items[REQUEST_STARTED] != null)
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
						// HttpContext.Current.ApplicationInstance will be null if using Classic mode
						if (GetIISVersion() >= 7 && !HttpRuntime.UsingIntegratedPipeline)
							throw new ApplicationException("Amnesia requires that the ASP.NET application pool use Integrated Pipeline mode");

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

		/// <summary>
		/// Gets the current version of IIS.  Must be called within worker process
		/// </summary>
		/// <returns>The current version of IIS running</returns>
		private static int GetIISVersion()
		{
			int version;
			using (Process process = Process.GetCurrentProcess())
			{
				using (ProcessModule mainModule = process.MainModule)
				{
					// main module would be w3wp
					version = mainModule.FileVersionInfo.FileMajorPart;
				}
			}

			return version;
		}
	}
}
