using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Amnesia
{
	[Serializable]
	class SerializableLog : ILog
	{
		[NonSerialized]
		DateTime startTime = DateTime.Now;

		[NonSerialized]
		bool useRelativeTimestamps;

		List<string> entries = new List<string>();

		public SerializableLog()
			: this(true)
		{
		}

		public SerializableLog(bool useRelativeTimestamps)
		{
			this.useRelativeTimestamps = useRelativeTimestamps;
		}

		public void Write(string messageFormat, params object[] args)
		{
			string entry;

			if (useRelativeTimestamps)
				entry = string.Format("[{0}] ", (DateTime.Now - startTime)) + " " + string.Format(messageFormat, args);
			else
				entry = string.Format("[{0:h:mm:ss.fffffff}] ", DateTime.Now) + " " + string.Format(messageFormat, args);

			lock(entries)
				entries.Add(entry);
		}

		public IEnumerable<string> Entries
		{
			get { return entries; }
		}

		public void CopyInto(ILog other)
		{
			lock (entries)
			{
				foreach (string entry in entries)
					other.Write(entry);
			}
		}
	}

}
