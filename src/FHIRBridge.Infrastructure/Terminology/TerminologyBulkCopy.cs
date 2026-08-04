using System.Data;
using Microsoft.Data.SqlClient;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>Streams pre-projected rows straight into SQL Server via SqlBulkCopy, for terminology imports
/// where the source dataset (RF2, ICD-10-CM order file, ...) is too large to buffer via EF Core change
/// tracking. Shared by every terminology import service (SNOMED CT, ICD-10, ...).</summary>
internal static class TerminologyBulkCopy
{
    private const int BatchSize = 20000;

    public static async Task WriteAsync(SqlConnection connection, SqlTransaction transaction, string destinationTable,
        string[] columns, IEnumerable<object?[]> rows, CancellationToken cancellationToken)
    {
        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = destinationTable,
            BatchSize = BatchSize,
            BulkCopyTimeout = 600,
        };
        foreach (var column in columns) bulkCopy.ColumnMappings.Add(column, column);
        using var reader = new ArrayRowDataReader(columns, rows.GetEnumerator());
        await bulkCopy.WriteToServerAsync(reader, cancellationToken);
    }

    /// <summary>Minimal streaming IDataReader over pre-projected object[] rows, so SqlBulkCopy can read
    /// directly from a lazily-parsed sequence without ever materializing it into a DataTable/list.</summary>
    private sealed class ArrayRowDataReader : IDataReader
    {
        private readonly string[] _columns;
        private readonly IEnumerator<object?[]> _rows;
        private object?[] _current = [];

        public ArrayRowDataReader(string[] columns, IEnumerator<object?[]> rows)
        {
            _columns = columns;
            _rows = rows;
        }

        public int FieldCount => _columns.Length;
        public object this[int i] => GetValue(i);
        public object this[string name] => GetValue(GetOrdinal(name));
        public int Depth => 0;
        public bool IsClosed => false;
        public int RecordsAffected => -1;

        public bool Read()
        {
            if (!_rows.MoveNext()) return false;
            _current = _rows.Current;
            return true;
        }

        public bool NextResult() => false;
        public void Close() { }
        public void Dispose() => _rows.Dispose();

        public string GetName(int i) => _columns[i];
        public int GetOrdinal(string name)
        {
            var index = Array.IndexOf(_columns, name);
            if (index < 0) throw new IndexOutOfRangeException(name);
            return index;
        }

        public object GetValue(int i) => _current[i] ?? DBNull.Value;
        public bool IsDBNull(int i) => _current[i] is null;

        public int GetValues(object[] values)
        {
            var count = Math.Min(values.Length, _current.Length);
            for (var i = 0; i < count; i++) values[i] = GetValue(i);
            return count;
        }

        public bool GetBoolean(int i) => (bool)_current[i]!;
        public byte GetByte(int i) => (byte)_current[i]!;
        public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
        public char GetChar(int i) => (char)_current[i]!;
        public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
        public IDataReader GetData(int i) => throw new NotSupportedException();
        public string GetDataTypeName(int i) => _current[i]?.GetType().Name ?? "Object";
        public DateTime GetDateTime(int i) => (DateTime)_current[i]!;
        public decimal GetDecimal(int i) => (decimal)_current[i]!;
        public double GetDouble(int i) => (double)_current[i]!;
        public Type GetFieldType(int i) => _current[i]?.GetType() ?? typeof(object);
        public float GetFloat(int i) => (float)_current[i]!;
        public Guid GetGuid(int i) => (Guid)_current[i]!;
        public short GetInt16(int i) => (short)_current[i]!;
        public int GetInt32(int i) => (int)_current[i]!;
        public long GetInt64(int i) => (long)_current[i]!;
        public string GetString(int i) => (string)_current[i]!;
        public DataTable? GetSchemaTable() => null;
    }
}
