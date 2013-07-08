using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.IO;
using System.Threading;
using System.Web;

namespace Amnesia
{
	internal interface ICommand
	{
		Handler.Response Execute(HttpContext ctx);
	}

	[Serializable]
	abstract internal class Command<TResponse> : ICommand
		where TResponse: Handler.Response, new()
	{
		TResponse response;

		/// <summary>
		/// Override in each command subclass.
		/// </summary>
		public abstract void Execute(HttpContext ctx);

		/// <summary>
		/// The response of the command
		/// </summary>
		internal TResponse Response
		{
			get
			{
				if (response == null)
					response = new TResponse(); 
				
				return response;
			}
		}

		public TResponse Send(string serviceUrl)
		{
			return Send(serviceUrl, Settings.Current.Timeout);
		}

		public TResponse Send(string serviceUrl, TimeSpan retryPeriodOnServiceUnavailable)
		{
			string reqPayload = SerializationUtil.SerializeBase64(this);

			DateTime startTime = DateTime.Now;

		RETRY:
			HttpWebRequest http = (HttpWebRequest)WebRequest.Create(serviceUrl + "?" + GetType().Name);
			http.Method = "POST";
			http.Timeout = 10 * 60 * 1000;

			//if(Amnesia.Settings.Current.AuthenticationMode == AuthenticationMode.Windows)
			//    http.Credentials = System.Net.CredentialCache.DefaultNetworkCredentials;

			using (StreamWriter writer = new StreamWriter(http.GetRequestStream()))
			{
				writer.Write(reqPayload);
				writer.Close();
			}

			string respPayload;
			try
			{
				using (StreamReader reader = new StreamReader(http.GetResponse().GetResponseStream()))
					respPayload = reader.ReadToEnd();
			}
			catch (WebException err)
			{
				if (err.Response != null && ((HttpWebResponse)err.Response).StatusCode == HttpStatusCode.ServiceUnavailable && (DateTime.Now - startTime) < retryPeriodOnServiceUnavailable)
				{
					// wait a moment then retry
					Thread.Sleep(1000);
					goto RETRY;
				}

				throw;
			}
			object response = SerializationUtil.DeserializeBase64(respPayload);

			if (response is Handler.ErrorResponse)
			{
				var errorResponse = (Handler.ErrorResponse)response;
				throw new ApplicationException(string.Format("Amnesia Handler Error: {0}\nException: {1}\nStack Track: {2}", errorResponse.Message, errorResponse.ExceptionType, errorResponse.StackTrace));
			}

			return (TResponse)response;
		}

		Handler.Response ICommand.Execute(HttpContext ctx)
		{
			this.Execute(ctx);
			return this.Response;
		}
	}
}
