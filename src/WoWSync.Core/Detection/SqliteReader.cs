using System.Buffers.Binary;
using System.Text;

namespace WoWSync.Core.Detection;

/// <summary>
/// Minimal read-only reader for SQLite database files (format 3), in managed code so WoWSync ships no
/// native SQLite library. Reads every row of every table, which is all Drive detection needs.
/// Supports table b-trees (interior and leaf pages), overflow pages and UTF-8/UTF-16 text.
/// Not supported (not needed here): WITHOUT ROWID tables, and changes still in a -wal file.
/// </summary>
public sealed class SqliteReader
{
    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();

    private readonly byte[] _db;
    private readonly int _pageSize;
    private readonly int _usable;
    private readonly Encoding _text;

    public sealed record Table(string Name, IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

    public SqliteReader(byte[] database)
    {
        _db = database;
        if (_db.Length < 100 || !_db.AsSpan(0, 16).SequenceEqual(Magic))
            throw new FormatException("Not an SQLite database.");
        int ps = BinaryPrimitives.ReadUInt16BigEndian(_db.AsSpan(16, 2));
        _pageSize = ps == 1 ? 65536 : ps;
        if (_pageSize < 512 || (_pageSize & (_pageSize - 1)) != 0) throw new FormatException("Bad SQLite page size.");
        _usable = _pageSize - _db[20];
        _text = BinaryPrimitives.ReadUInt32BigEndian(_db.AsSpan(56, 4)) switch
        {
            2 => Encoding.Unicode,
            3 => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8,
        };
    }

    /// <summary>Every ordinary table with its column names (from its CREATE TABLE) and rows.</summary>
    public IReadOnlyList<Table> ReadTables()
    {
        var tables = new List<Table>();
        foreach (var row in ReadRows(1))
        {
            // sqlite_schema: type, name, tbl_name, rootpage, sql
            if (row.Values.Length < 5 || row.Values[0] as string != "table") continue;
            var name = row.Values[1] as string ?? "";
            if (name.StartsWith("sqlite_", StringComparison.Ordinal)) continue;
            var sql = row.Values[4] as string ?? "";
            if (sql.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase)) continue;
            int root = Convert.ToInt32(row.Values[3] ?? 0);
            if (root <= 0) continue;

            var columns = ColumnNames(sql);
            var realColumns = RealAffinityColumns(sql);
            int rowidAlias = RowidAliasIndex(sql, columns);
            var rows = new List<object?[]>();
            foreach (var r in ReadRows(root))
            {
                var values = r.Values;
                if (rowidAlias >= 0 && rowidAlias < values.Length && values[rowidAlias] == null)
                    values[rowidAlias] = r.RowId; // INTEGER PRIMARY KEY is stored as the rowid
                // SQLite stores whole-number REAL values as integers on disk; restore them as REAL.
                foreach (var c in realColumns)
                    if (c < values.Length && values[c] is long l) values[c] = (double)l;
                rows.Add(values);
            }
            tables.Add(new Table(name, columns, rows));
        }
        return tables;
    }

    private readonly record struct Row(long RowId, object?[] Values);

    private IEnumerable<Row> ReadRows(int rootPage)
    {
        var pending = new Stack<int>();
        var seen = new HashSet<int>();
        pending.Push(rootPage);
        while (pending.Count > 0)
        {
            int page = pending.Pop();
            if (!seen.Add(page)) throw new FormatException("SQLite b-tree loop.");
            int start = PageOffset(page);
            int header = page == 1 ? start + 100 : start;
            byte type = _db[header];
            int cells = BinaryPrimitives.ReadUInt16BigEndian(_db.AsSpan(header + 3, 2));

            if (type == 0x05) // interior table page
            {
                int right = (int)BinaryPrimitives.ReadUInt32BigEndian(_db.AsSpan(header + 8, 4));
                var children = new List<int>();
                for (int i = 0; i < cells; i++)
                {
                    int cell = start + BinaryPrimitives.ReadUInt16BigEndian(_db.AsSpan(header + 12 + 2 * i, 2));
                    children.Add((int)BinaryPrimitives.ReadUInt32BigEndian(_db.AsSpan(cell, 4)));
                }
                children.Add(right);
                for (int i = children.Count - 1; i >= 0; i--) pending.Push(children[i]); // keep rowid order
            }
            else if (type == 0x0D) // leaf table page
            {
                for (int i = 0; i < cells; i++)
                {
                    int pos = start + BinaryPrimitives.ReadUInt16BigEndian(_db.AsSpan(header + 8 + 2 * i, 2));
                    long payloadSize = ReadVarint(ref pos);
                    long rowId = ReadVarint(ref pos);
                    yield return new Row(rowId, DecodeRecord(ReadPayload(pos, payloadSize)));
                }
            }
            else
            {
                throw new FormatException($"Unexpected SQLite page type 0x{type:X2} in a table.");
            }
        }
    }

    private byte[] ReadPayload(int pos, long size)
    {
        if (size < 0 || size > int.MaxValue) throw new FormatException("Bad SQLite payload size.");
        int total = (int)size;
        int maxLocal = _usable - 35;
        int local;
        if (total <= maxLocal) local = total;
        else
        {
            int minLocal = (_usable - 12) * 32 / 255 - 23;
            int k = minLocal + (total - minLocal) % (_usable - 4);
            local = k <= maxLocal ? k : minLocal;
        }

        var payload = new byte[total];
        Need(pos, local);
        Array.Copy(_db, pos, payload, 0, local);
        int copied = local;
        if (copied < total)
        {
            Need(pos + local, 4);
            int next = (int)BinaryPrimitives.ReadUInt32BigEndian(_db.AsSpan(pos + local, 4));
            var seen = new HashSet<int>();
            while (copied < total)
            {
                if (next == 0 || !seen.Add(next)) throw new FormatException("Broken SQLite overflow chain.");
                int off = PageOffset(next);
                int chunk = Math.Min(_usable - 4, total - copied);
                Need(off + 4, chunk);
                Array.Copy(_db, off + 4, payload, copied, chunk);
                copied += chunk;
                next = (int)BinaryPrimitives.ReadUInt32BigEndian(_db.AsSpan(off, 4));
            }
        }
        return payload;
    }

    private object?[] DecodeRecord(byte[] rec)
    {
        int pos = 0;
        long headerSize = ReadVarint(rec, ref pos);
        var types = new List<long>();
        while (pos < headerSize) types.Add(ReadVarint(rec, ref pos));
        int body = (int)headerSize;
        var values = new object?[types.Count];
        for (int i = 0; i < types.Count; i++)
        {
            long t = types[i];
            switch (t)
            {
                case 0: values[i] = null; break;
                case 1: values[i] = (long)(sbyte)rec[body]; body += 1; break;
                case 2: values[i] = (long)BinaryPrimitives.ReadInt16BigEndian(rec.AsSpan(body)); body += 2; break;
                case 3: values[i] = (long)((rec[body] << 16 | rec[body + 1] << 8 | rec[body + 2]) << 8 >> 8); body += 3; break;
                case 4: values[i] = (long)BinaryPrimitives.ReadInt32BigEndian(rec.AsSpan(body)); body += 4; break;
                case 5:
                    long v48 = 0;
                    for (int b = 0; b < 6; b++) v48 = v48 << 8 | rec[body + b];
                    values[i] = v48 << 16 >> 16; body += 6; break;
                case 6: values[i] = BinaryPrimitives.ReadInt64BigEndian(rec.AsSpan(body)); body += 8; break;
                case 7: values[i] = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(rec.AsSpan(body))); body += 8; break;
                case 8: values[i] = 0L; break;
                case 9: values[i] = 1L; break;
                case >= 12 when t % 2 == 0:
                    int blobLen = (int)((t - 12) / 2);
                    values[i] = rec.AsSpan(body, blobLen).ToArray(); body += blobLen; break;
                case >= 13:
                    int textLen = (int)((t - 13) / 2);
                    values[i] = _text.GetString(rec, body, textLen); body += textLen; break;
                default: throw new FormatException($"Unknown SQLite serial type {t}.");
            }
        }
        return values;
    }

    private int PageOffset(int page)
    {
        long off = (long)(page - 1) * _pageSize;
        if (page < 1 || off + _pageSize > _db.Length) throw new FormatException("SQLite page out of range.");
        return (int)off;
    }

    private void Need(int pos, int count)
    {
        if (pos < 0 || count < 0 || pos + count > _db.Length) throw new FormatException("Truncated SQLite file.");
    }

    private long ReadVarint(ref int pos) => ReadVarint(_db, ref pos);

    private static long ReadVarint(byte[] data, ref int pos)
    {
        long result = 0;
        for (int i = 0; i < 9; i++)
        {
            if (pos >= data.Length) throw new FormatException("Truncated SQLite varint.");
            byte b = data[pos++];
            if (i == 8) return result << 8 | b;
            result = result << 7 | (long)(b & 0x7F);
            if ((b & 0x80) == 0) return result;
        }
        return result;
    }

    /// <summary>Column names from a CREATE TABLE statement (table constraints skipped).</summary>
    internal static List<string> ColumnNames(string createSql)
    {
        var names = new List<string>();
        int open = createSql.IndexOf('(');
        int close = createSql.LastIndexOf(')');
        if (open < 0 || close <= open) return names;
        foreach (var part in SplitTopLevel(createSql[(open + 1)..close]))
        {
            var def = part.Trim();
            if (def.Length == 0) continue;
            var first = FirstToken(def);
            if (first.ToUpperInvariant() is "PRIMARY" or "UNIQUE" or "CHECK" or "FOREIGN" or "CONSTRAINT") continue;
            names.Add(first);
        }
        return names;
    }

    /// <summary>Indexes of columns with REAL affinity (declared type has REAL/FLOA/DOUB and no INT).</summary>
    private static HashSet<int> RealAffinityColumns(string sql)
    {
        var result = new HashSet<int>();
        int open = sql.IndexOf('(');
        int close = sql.LastIndexOf(')');
        if (open < 0 || close <= open) return result;
        int index = 0;
        foreach (var part in SplitTopLevel(sql[(open + 1)..close]))
        {
            var def = part.Trim();
            if (def.Length == 0) continue;
            var first = FirstToken(def);
            if (first.ToUpperInvariant() is "PRIMARY" or "UNIQUE" or "CHECK" or "FOREIGN" or "CONSTRAINT") continue;
            var type = def[Math.Min(def.Length, def.IndexOf(first, StringComparison.Ordinal) + first.Length + (def[0] is '"' or '`' or '[' ? 1 : 0))..].ToUpperInvariant();
            if (!type.Contains("INT") && (type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB"))) result.Add(index);
            index++;
        }
        return result;
    }

    private static int RowidAliasIndex(string sql, List<string> columns)
    {
        int open = sql.IndexOf('(');
        int close = sql.LastIndexOf(')');
        if (open < 0 || close <= open) return -1;
        int index = 0;
        foreach (var part in SplitTopLevel(sql[(open + 1)..close]))
        {
            var def = part.Trim();
            if (def.Length == 0) continue;
            var first = FirstToken(def);
            if (first.ToUpperInvariant() is "PRIMARY" or "UNIQUE" or "CHECK" or "FOREIGN" or "CONSTRAINT") continue;
            var upper = " " + string.Join(' ', def.ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) + " ";
            if (upper.Contains(" INTEGER PRIMARY KEY")) return index;
            index++;
        }
        return -1;
    }

    private static string FirstToken(string def)
    {
        def = def.TrimStart();
        if (def.Length > 0 && def[0] is '"' or '`' or '[')
        {
            char end = def[0] == '[' ? ']' : def[0];
            int stop = def.IndexOf(end, 1);
            return stop > 0 ? def[1..stop] : def[1..];
        }
        int space = def.IndexOfAny([' ', '\t', '\r', '\n']);
        return space < 0 ? def : def[..space];
    }

    private static IEnumerable<string> SplitTopLevel(string s)
    {
        int depth = 0, start = 0;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '\'' or '"' or '`') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) { yield return s[start..i]; start = i + 1; }
        }
        yield return s[start..];
    }
}
