using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.IO;
using System.Threading;

namespace Amnesia
{
	internal interface ICommand
	{
		object Execute();
	}

	[Serializable]
	abstract internal class Command<TResponse> : ICommand
	{
		internal abstract TResponse Execute();

		public TResponse Send(string serviceUrl)
		{
			return Send(serviceUrl, TimeSpan.Zero);
		}

		public TResponse Send(string serviceUrl, TimeSpan retryPeriodOnServiceUnavailable)
		{
			string reqPayload = SerializationUtil.SerializeBase64(this);

			DateTime startTime = DateTime.Now;

		RETRY:
			HttpWebRequest http = (HttpWebRequest)WebRequest.Create(serviceUrl);
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
				if (((HttpWebResponse)err.Response).StatusCode == HttpStatusCode.ServiceUnavailable && (DateTime.Now - startTime) < retryPeriodOnServiceUnavailable)
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

		object ICommand.Execute()
		{
			return this.Execute();
		}
	}
}
