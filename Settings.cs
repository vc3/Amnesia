using System.Configuration;

namespace Amnesia
{
	public class Settings : IConfigurationSectionHandler
	{
		string handlerPath;
		string stateFile;

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

			return this;
		}

		#endregion

	}
}
