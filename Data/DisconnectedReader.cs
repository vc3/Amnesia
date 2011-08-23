using System;
using System.Data;
using System.Collections;

namespace Amnesia.Data
{
	class DisconnectedReader : IDataReader, IEnumerable, ICollection
	{

		#region FieldInfo Class

		class FieldInfo
		{
			internal string Name;
			internal Type Type;
			internal string DataTypeName;
		}

		#endregion

		#region Fields

		ArrayList rows;
		FieldInfo[] fields;
		DataTable schema;
		Hashtable ordinals;
		DisconnectedReader nextResult;
		int recordIndex;
		int lastIndex;

		#endregion

		#region Constructors

		/// <summary>
		/// Caches a data reader to enable disconnected data access via the IDataReader interface.
		/// </summary>
		/// <param name="reader">The IDataReader to cache.</param>
		public DisconnectedReader(IDataReader reader)
		{
			// Cache field information
			this.fields = new FieldInfo[reader.FieldCount];
			this.ordinals = new Hashtable(StringComparer.OrdinalIgnoreCase);
			for (int index = 0; index < fields.Length; index++)
			{
				FieldInfo field = new FieldInfo();
				field.Name = reader.GetName(index);
				field.Type = reader.GetFieldType(index);
				field.DataTypeName = reader.GetDataTypeName(index);
				fields[index] = field;
				if (!ordinals.Contains(field.Name))
					ordinals.Add(field.Name, index);
			}

			// Cache schema info
			schema = reader.GetSchemaTable();

			// Cache row data
			rows = new ArrayList();
			while (reader.Read())
			{
				object[] values = new object[fields.Length];
				reader.GetValues(values);
				rows.Add(values);
			}

			// Cache additional results
			if (reader.NextResult())
				nextResult = new DisconnectedReader(reader);

			// Close the reader once all data has been cached
			else
				reader.Dispose();

			// Set the record index before the first record;
			this.recordIndex = -1;

			this.lastIndex = rows.Count - 1;
		}

		public DisconnectedReader(IDataReader reader, int startIndex, int limit)
			: this(reader)
		{
			if (limit > 0)
				this.SetRange(startIndex, limit);
		}

		#endregion

		#region Methods

		public void SetRange(int startIndex, int limit)
		{
			if (!(rows.Count == 0 && startIndex == 0))		// with no rows in reader, do this check to make sure exception is not thrown when unnecessary
				if (startIndex < 0 || startIndex >= rows.Count)
					throw new IndexOutOfRangeException("Start Index must be within the range of available data.");

			if (limit < 0)
				throw new ArgumentOutOfRangeException("Data limit must be greater than or equal to zero.");

			this.recordIndex = startIndex - 1;
			// do some bounds checking
			if (startIndex + limit <= rows.Count)
				this.lastIndex = startIndex + limit - 1;
			else
				this.lastIndex = rows.Count - 1;
		}

		#endregion

		#region IDataRecord Implementation

		int IDataRecord.FieldCount
		{
			get
			{
				return fields.Length;
			}
		}

		bool IDataRecord.GetBoolean(int index)
		{
			return (Boolean)((object[])rows[recordIndex])[index];
		}

		byte IDataRecord.GetByte(int index)
		{
			return (Byte)((object[])rows[recordIndex])[index];
		}

		long IDataRecord.GetBytes(int index, long fieldOffset, byte[] buffer, int bufferOffset, int length)
		{
			throw new NotImplementedException();
		}

		char IDataRecord.GetChar(int index)
		{
			return (Char)((object[])rows[recordIndex])[index];
		}

		long IDataRecord.GetChars(int index, long fieldOffset, char[] buffer, int bufferOffset, int length)
		{
			throw new NotImplementedException();
		}

		IDataReader IDataRecord.GetData(int index)
		{
			throw new NotImplementedException();
		}

		string IDataRecord.GetDataTypeName(int index)
		{
			return fields[index].DataTypeName;
		}

		DateTime IDataRecord.GetDateTime(int index)
		{
			return (DateTime)((object[])rows[recordIndex])[index];
		}

		decimal IDataRecord.GetDecimal(int index)
		{
			return (Decimal)((object[])rows[recordIndex])[index];
		}

		double IDataRecord.GetDouble(int index)
		{
			return (Double)((object[])rows[recordIndex])[index];
		}

		Type IDataRecord.GetFieldType(int index)
		{
			return fields[index].Type;
		}

		float IDataRecord.GetFloat(int index)
		{
			return (Single)((object[])rows[recordIndex])[index];
		}

		Guid IDataRecord.GetGuid(int index)
		{
			return (Guid)((object[])rows[recordIndex])[index];
		}

		short IDataRecord.GetInt16(int index)
		{
			return (Int16)((object[])rows[recordIndex])[index];
		}

		int IDataRecord.GetInt32(int index)
		{
			return (Int32)((object[])rows[recordIndex])[index];
		}

		long IDataRecord.GetInt64(int index)
		{
			return (Int64)((object[])rows[recordIndex])[index];
		}

		string IDataRecord.GetName(int index)
		{
			return fields[index].Name;
		}

		int IDataRecord.GetOrdinal(string name)
		{
			try
			{
				return (int)ordinals[name];
			}
			catch
			{
				throw new ArgumentException("Field '" + name + "' does not exist in the current result set.");
			}
		}

		string IDataRecord.GetString(int index)
		{
			return ((object[])rows[recordIndex])[index].ToString();
		}

		object IDataRecord.GetValue(int index)
		{
			return ((object[])rows[recordIndex])[index];
		}

		int IDataRecord.GetValues(object[] values)
		{
			((object[])rows[recordIndex]).CopyTo(values, 0);
			return fields.Length;
		}

		bool IDataRecord.IsDBNull(int index)
		{
			return ((object[])rows[recordIndex])[index] == DBNull.Value;
		}

		#endregion

		#region IDataReader Implementation

		void IDataReader.Close()
		{
			rows = null;
			nextResult = null;
			fields = null;
			ordinals = null;
			recordIndex = -1;
		}

		int IDataReader.Depth
		{
			get
			{
				return 0;
			}
		}

		DataTable IDataReader.GetSchemaTable()
		{
			return schema;
		}

		bool IDataReader.IsClosed
		{
			get
			{
				return rows == null;
			}
		}

		bool IDataReader.NextResult()
		{
			if (nextResult == null)
				return false;

			this.fields = nextResult.fields;
			this.ordinals = nextResult.ordinals;
			this.rows = nextResult.rows;
			this.schema = nextResult.schema;
			this.recordIndex = -1;
			this.nextResult = nextResult.nextResult;
			this.lastIndex = rows.Count - 1;

			return true;
		}


		bool IDataReader.Read()
		{
			return ++recordIndex <= this.lastIndex;
		}

		int IDataReader.RecordsAffected
		{
			get
			{
				return 0;
			}
		}

		object IDataRecord.this[int index]
		{
			get
			{
				return ((object[])rows[recordIndex])[index];
			}
		}

		object IDataRecord.this[string name]
		{
			get
			{
				return ((object[])rows[recordIndex])[(int)ordinals[name]];
			}
		}

		#endregion

		#region IDisposable Implementation

		void IDisposable.Dispose()
		{
			rows = null;
			nextResult = null;
			fields = null;
			ordinals = null;
			recordIndex = -1;
		}

		#endregion

		#region IEnumerable Implementation

		IEnumerator IEnumerable.GetEnumerator()
		{
			return new System.Data.Common.DbEnumerator(this);
		}

		#endregion

		#region Implementation of ICollection
		public void CopyTo(System.Array array, int index)
		{
			rows.CopyTo(array, index);
		}

		public bool IsSynchronized
		{
			get
			{
				return rows.IsSynchronized;
			}
		}

		public int Count
		{
			get
			{
				return rows.Count;
			}
		}

		public object SyncRoot
		{
			get
			{
				return rows.SyncRoot;
			}
		}
		#endregion

		#region Implementation of IEnumerable
		public System.Collections.IEnumerator GetEnumerator()
		{
			return rows.GetEnumerator();
		}
		#endregion

	}
}
