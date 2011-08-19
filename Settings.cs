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

		public bool DebugOnUnexpectedRollback { get; private set; }

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

			if (section.Attributes["debugOnUnexpectedRollback"] != null)
				DebugOnUnexpectedRollback = bool.Parse(section.Attributes["debugOnUnexpectedRollback"].Value);

			return this;
		}

		#endregion

	}
}
