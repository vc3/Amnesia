using System;
using System.Runtime.Serialization.Formatters.Binary;
using System.IO;
using System.Collections;
using System.Reflection;
using System.Xml.Serialization;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;

namespace Amnesia
{
	public class SerializationUtil
	{
		static readonly string NullBase64 = "";

		#region FixedByteArray
		class FixedByteArray
		{
			byte[] data;
			int length;

			public FixedByteArray() { }

			public FixedByteArray(byte[] data, int length)
			{
				this.data = data;
				this.length = length;
			}

			public byte[] Data
			{
				get
				{
					return data;
				}
				set
				{
					data = value;
				}
			}

			public int Length
			{
				get
				{
					return length;
				}
				set
				{
					length = value;
				}
			}
		}
		#endregion

		#region Bytes <--> Object
		/// <summary>
		/// Utility method for serializing objects into a binary format.
		/// <seealso cref="Deserialize(byte[], ISurrogateSelector)"/>
		/// </summary>
		static public byte[] Serialize(object o, ISurrogateSelector selector)
		{
			if (o == null)
				return new byte[0];

			BinaryFormatter formatter = new BinaryFormatter();

			if (selector != null)
				formatter.SurrogateSelector = selector;

			MemoryStream s = new MemoryStream();

			formatter.Serialize(s, o);
			s.Close();

			return s.GetBuffer();
		}

		static FixedByteArray SerializeToFixedByteArray(object o, ISurrogateSelector selector)
		{
			if (o == null)
				return new FixedByteArray(new byte[0], 0);

			FixedByteArray array = new FixedByteArray();

			BinaryFormatter formatter = new BinaryFormatter();

			if (selector != null)
				formatter.SurrogateSelector = selector;

			MemoryStream s = new MemoryStream();

			formatter.Serialize(s, o);
			array.Length = (int)s.Length;
			s.Close();

			array.Data = s.GetBuffer();
			return array;
		}


		/// <summary>
		/// Utility method for serializing objects into a binary format.
		/// <seealso cref="Deserialize(byte[])"/>
		/// </summary>
		static public byte[] Serialize(object o)
		{
			return Serialize(o, null);
		}


		/// <summary>
		/// Utility method for de-serializing objects into a binary format
		/// <seealso cref="Serialize(object, ISurrogateSelector)"/>
		/// </summary>
		static public object Deserialize(byte[] data, ISurrogateSelector selector)
		{
			if (data.Length == 0)
				return null;

			BinaryFormatter formatter = new BinaryFormatter();

			if (selector != null)
				formatter.SurrogateSelector = selector;

			MemoryStream s = new MemoryStream(data);

			object o = formatter.Deserialize(s);
			s.Close();

			return o;
		}

		/// <summary>
		/// Utility method for de-serializing objects into a binary format
		/// <seealso cref="Serialize(object)"/>
		/// </summary>
		static public object Deserialize(byte[] data)
		{
			return Deserialize(data, null);
		}
		#endregion

		#region Base64 String <--> Object
		/// <summary>
		/// Utility method for serializing objects into a base64 format.
		/// <seealso cref="DeserializeBase64(string, ISurrogateSelector)"/>.  Null object references will 
		/// be serialized correctly without causing an exception.
		/// </summary>
		static public string SerializeBase64(object o, ISurrogateSelector selector)
		{
			if (o == null)
				return NullBase64;

			// convert to bytes, without extra padding at the end			
			FixedByteArray array = SerializeToFixedByteArray(o, selector);

			// convert bytes to text
			return Convert.ToBase64String(array.Data, 0, array.Length);
		}

		/// <summary>
		/// Utility method for serializing objects into a base64 format.
		/// <seealso cref="DeserializeBase64(string)"/>.  Null object references will 
		/// be serialized correctly without causing an exception.
		/// </summary>
		static public string SerializeBase64(object o)
		{
			return SerializeBase64(o, null);
		}

		/// <summary>
		/// Utility method for de-serializing objects from a base64 format.
		/// <seealso cref="SerializeBase64(object, ISurrogateSelector)"/>. Null object references will 
		/// be deserialized correctly without causing an exception.
		/// </summary>
		static public object DeserializeBase64(string data, ISurrogateSelector selector)
		{
			if (data == NullBase64)
				return null;

			// convert text to bytes
			byte[] bytes = Convert.FromBase64String(data);

			// convert bytes to object
			return Deserialize(bytes, selector);
		}

		/// <summary>
		/// Utility method for de-serializing objects from a base64 format.
		/// <seealso cref="SerializeBase64(object)"/>. Null object references will 
		/// be deserialized correctly without causing an exception.
		/// </summary>
		static public object DeserializeBase64(string data)
		{
			return DeserializeBase64(data, null);
		}
		#endregion


		#region Misc
		/// <summary>
		/// Removes non-public method targets from a delegate so that they
		/// can be safely serialized.
		/// </summary>
		/// <param name="d">The multicast or singlecast delegate to process</param>
		/// <returns>A delegate with only the public methods or null if all methods are non-public</returns>
		public static Delegate MakeSerializable(Delegate d)
		{
			if (d == null)
				return null;

			Delegate[] children = d.GetInvocationList();

			if (children.Length == 0)
				return null;
			else if (children.Length == 1 && d == children[0])
			{
				// Non-combined delegate
				if (d.Method.IsPublic)
					return d;
				else
					return null;
			}
			else
			{
				ArrayList publicChildren = new ArrayList(children.Length);

				foreach (Delegate child in children)
				{
					Delegate publicChild = MakeSerializable(child);

					if (publicChild != null)
						publicChildren.Add(publicChild);
				}

				if (publicChildren.Count == 0)
					return null;
				else if (publicChildren.Count == 1)
					return (Delegate)publicChildren[0];
				else
				{
					Delegate[] publicChildrenArray = new Delegate[publicChildren.Count];
					for (int i = 0; i < publicChildren.Count; ++i)
						publicChildrenArray[i] = (Delegate)publicChildren[i];

					return Delegate.Combine(publicChildrenArray);
				}
			}
		}

		/// <summary>
		/// Wraps an ISerializationSurrogate surrogate with special one to enable cyclical references during serialization
		/// if hotfix http://support.microsoft.com/kb/931634, or later is installed.  Wrapping the surrogate
		/// should fix the "The object with ID X was referenced in a fixup but does not exist." error that occurs during
		/// deserialization.
		/// </summary>
		public static ISerializationSurrogate EnableCyclicalReferences(ISerializationSurrogate surrogate)
		{
			// Look for the FormatterServices.GetSurrogateForCyclicalReference() method
			// This method cannot be called directly because it may not exist in all environments and currently
			// only exists on the Server-version of Windows.
			foreach (MethodInfo method in typeof(FormatterServices).GetMethods(BindingFlags.Public | BindingFlags.Static))
			{
				if (method.Name == "GetSurrogateForCyclicalReference")
				{
					return (ISerializationSurrogate)method.Invoke(null, new object[] { surrogate });
				}
			}

			return surrogate;
		}

		#endregion
	}
}
