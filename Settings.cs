using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Configuration;

namespace Amnesia
{
	public class Settings : IConfigurationSectionHandler
	{
		[ConfigurationProperty("handlerPath", IsRequired = false, DefaultValue="/Amnesia.axd")]
		public string HandlerPath { get; set; }

		public static Settings Current
		{
			get
			{
				Settings current = (Settings)System.Configuration.ConfigurationManager.GetSection("amnesia");

				if (current == null)
				{
					current = new Settings();
				}

				return current;
			}
		}

		#region IConfigurationSectionHandler Members

		public object Create(object parent, object configContext, System.Xml.XmlNode section)
		{
			if (section.Attributes["handlerPath"] != null)
				HandlerPath = section.Attributes["handlerPath"].Value;

			return this;
		}

		#endregion
	}
}
