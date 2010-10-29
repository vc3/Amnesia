using System.Configuration;

namespace Amnesia
{
	public class Settings : IConfigurationSectionHandler
	{
		string handlerPath;

		public string HandlerPath
		{
			get
			{
				return handlerPath ?? "/Amnesia.axd";
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

			return this;
		}

		#endregion
	}
}
