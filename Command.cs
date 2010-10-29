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

			return (TResponse)SerializationUtil.DeserializeBase64(respPayload);
		}

		object ICommand.Execute()
		{
			return this.Execute();
		}
	}
}
