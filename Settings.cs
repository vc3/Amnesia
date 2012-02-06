using System.Configuration;
using System;

namespace Amnesia
{
	public class Settings : IConfigurationSectionHandler
	{
		string handlerPath;
		string stateFile;
		int timeout;

		public string HandlerPath
		{
			get
			{
				return handlerPath ?? "/Amnesia.axd";
			}
		}

		public string StateFile
		{
			get
			{
				return stateFile ?? "../Amnesia/State.amnesia";
			}
		}

		/// <summary>
		/// The number of seconds to wait when issuing a command
		/// </summary>
		public TimeSpan Timeout
		{
			get
			{
				return timeout == Int32.MinValue ? TimeSpan.Zero : new TimeSpan(0, 0, timeout);
			}
		}

		public static Settings Current
		{
			get
			{
				Settings current = (Settings)ConfigurationManager.GetSection("amnesia");
				
				if (current == null)
					current = new Settings();

				return current;
			}
		}

		#region IConfigurationSectionHandler Members

		public object Create(object parent, object configContext, System.Xml.XmlNode section)
		{
			if (section.Attributes["handlerPath"] != null)
				handlerPath = section.Attributes["handlerPath"].Value;

			if (section.Attributes["stateFile"] != null)
				stateFile = section.Attributes["stateFile"].Value;

			if (section.Attributes["timeout"] != null)
				timeout = Int32.Parse(section.Attributes["timeout"].Value);

			return this;
		}

		#endregion

	}
}
