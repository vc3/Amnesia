using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;
using System.Threading;

namespace Amnesia
{
	public class Module : IHttpModule
	{
		static object fieldsLock = new object();
		static object pauseLock = new object();

		static bool moduleRegistered = false;
		static int requestsExecuting = 0;
		static int freeRequests = -1;
		static bool paused = false;
		static AutoResetEvent requestsDone;

		void IHttpModule.Dispose()
		{
		}

		void IHttpModule.Init(HttpApplication context)
		{
			moduleRegistered = true;

			context.BeginRequest += context_BeginRequest;
			context.EndRequest += context_EndRequest;
		}

		void context_BeginRequest(object sender, EventArgs e)
		{
			lock (fieldsLock)
			{
				// Track that a request has been started
				++requestsExecuting;

				// Abort all requests when paused
				if (paused)
				{
					HttpContext.Current.Response.StatusCode = 503; // service unavailable
					HttpContext.Current.Response.Write("Amnesia is currently blocking requests. Try again in a few moments.");
					HttpContext.Current.Response.End();
					return;
				}
			}
		}

		void context_EndRequest(object sender, EventArgs e)
		{
			lock (fieldsLock)
			{
				// Track that a request is ending
				--requestsExecuting;

				// Notify any interested thread that there are no more requests executing
				if (requestsDone != null && requestsExecuting == freeRequests)
					requestsDone.Set();

				else if (requestsExecuting < freeRequests)
				{
					// DEADLOCK: The request that requested the lock has ended but all future requests are being
					// blocked so the lock can never be released!

					// Correctly written code should never get here but it seems like the best thing to do is to
					// release the lock to prevent a deadlock.
					ResumeRequests();
				}
			}
		}

		/// <summary>
		/// Stop all incoming ASP requests and wait for the current requests to complete. When
		/// this method returns, all requests (execpt for the current Amnesia one) will have
		/// completed and any future requests will be denied.
		/// </summary>
		internal static IDisposable LockWebServer(int timeoutMS, ILog log)
		{
			log = log ?? NullLog.Instance;

			log.Write("Acquiring web server lock...");
			PauseRequests(timeoutMS);
			log.Write("> Lock acquired");

			return new UndoableAction(delegate {
				log.Write("Releasing web server lock...");
				ResumeRequests();
				log.Write("> Lock released");
			});
		}

		/// <summary>
		/// Stop all incoming ASP requests and wait for the current requests to complete. When
		/// this method returns, all requests (execpt for the current Amnesia one) will have
		/// completed and any future requests will be denied.
		/// </summary>
		private static void PauseRequests(int timeoutMS)
		{
			if (!moduleRegistered)
				throw new InvalidOperationException(@"Amnesia's HTTP Module must be registered. Here's some XML that may help: <add name=""Amnesia"" type=""Amnesia.Module, Amnesia"" />");

			// Ensure only a single thread owns the pause lock.
			Monitor.Enter(pauseLock);

			//// Use a timeout here in case an ASP request is trying to acquire the pauseLock while another
			//// thread is simultaneously waiting for that request to complete.
			//if (!Monitor.TryEnter(pauseLock, timeoutMS))
			//    throw new TimeoutException("Could not lock web server after " + timeoutMS + " milliseconds");

			try
			{
				// At this point the current thread has been granted the pauseLock and can start blocking future requests as well as
				// wait for any current requests to complete.

				lock (fieldsLock)
				{
					// stop future requests from starting
					paused = true;

					// Take into account that the lock request may be occuring either from an ASP request or
					// from another thread that is not an ASP request.
					freeRequests = HttpContext.Current == null ? 0 : 1;

					// Wait for any requests that are currently executing to complete
					if (requestsExecuting > freeRequests)
					{
						// Let other threads know we're interested in knowing when we're idle and wait
						requestsDone = new AutoResetEvent(false);
					}
				}

				// Wait for other request threads to complete
				if (requestsDone != null)
				{
					requestsDone.WaitOne();
					requestsDone = null;
				}

				// Hold the pauseLock open until ResumeRequests()
			}
			catch (Exception)
			{
				// An unexpected error occured so release the pauseLock
				ResumeRequests();
				throw;
			}
		}

		/// <summary>
		/// Allow incoming ASP requests to be handled
		/// </summary>
		private static void ResumeRequests()
		{
			lock (fieldsLock)
			{
				paused = false;
				freeRequests = -1;

				// release the lock
				Monitor.Exit(pauseLock);
			}
		}
	}
}
