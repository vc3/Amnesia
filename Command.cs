using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.IO;

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
			string reqPayload = SerializationUtil.SerializeBase64(this);

			HttpWebRequest http = (HttpWebRequest)WebRequest.Create(serviceUrl);
			http.Method = "POST";

			//if(Amnesia.Settings.Current.AuthenticationMode == AuthenticationMode.Windows)
			//    http.Credentials = System.Net.CredentialCache.DefaultNetworkCredentials;

			using (StreamWriter writer = new StreamWriter(http.GetRequestStream()))
			{
				writer.Write(reqPayload);
				writer.Close();
			}

			string respPayload;
			using (StreamReader reader = new StreamReader(http.GetResponse().GetResponseStream()))
				respPayload = reader.ReadToEnd();

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
