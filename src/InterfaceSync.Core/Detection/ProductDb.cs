using System.Text;
using System.Text.RegularExpressions;

namespace InterfaceSync.Core.Detection;

/// <summary>One product Battle.net has installed, as recorded in product.db.</summary>
public sealed record BattleNetProduct(
    string Uid,
    string? ProductCode,
    string? InstallPath,
    string? GameFolder,
    string? Version,
    string? Family = null)
{
    /// <summary>
    /// Battle.net's product family ("wow") decides. Only when a product has no family recorded
    /// does the uid prefix count, so a future WoW uid without the "wow" prefix is still found.
    /// </summary>
    public bool IsWow => string.IsNullOrEmpty(Family)
        ? Uid.StartsWith("wow", StringComparison.OrdinalIgnoreCase)
        : Family.Equals("wow", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads Battle.net's install database (C:\ProgramData\Battle.net\Agent\product.db), a protobuf file.
///
/// Field numbers follow the community ProductDb.proto schema, verified against a real product.db
/// (Battle.net 2.53, Nov 2026):
/// <code>
///   Database        { repeated ProductInstall product_installs = 1; ... }
///   ProductInstall  { string uid = 1; string product_code = 2; UserSettings settings = 3; CachedProductState cached_product_state = 4; string product_family = 6; ... }
///   UserSettings    { string install_path = 1; ... string game_subfolder = 13; }
///   CachedProductState { BaseProductState base_product_state = 1; ... }
///   BaseProductState   { ... string current_version_str = 7; ... }
/// </code>
/// Values are always read from inside the same ProductInstall message, so a path is never paired with
/// another product's folder. If the folder isn't at the expected field number, the one string in that
/// product's settings shaped like a game folder ("_name_") is used.
/// </summary>
public static class ProductDb
{
    internal const int DatabaseProductInstall = 1;
    internal const int InstallUid = 1;
    internal const int InstallProductCode = 2;
    internal const int InstallSettings = 3;
    internal const int InstallCachedState = 4;
    internal const int InstallFamily = 6;
    internal const int SettingsInstallPath = 1;
    internal const int SettingsGameSubfolder = 13;
    internal const int CachedBaseState = 1;
    internal const int BaseCurrentVersionStr = 7;

    private static readonly Regex GameFolderPattern = new(@"^_[A-Za-z0-9_]+_$", RegexOptions.CultureInvariant);

    /// <summary>Every product in the file. Throws <see cref="FormatException"/> if the bytes aren't a valid database.</summary>
    public static IReadOnlyList<BattleNetProduct> Parse(ReadOnlySpan<byte> data)
    {
        var products = new List<BattleNetProduct>();
        foreach (var field in Protobuf.ReadMessage(data))
        {
            if (field.Number != DatabaseProductInstall || field.WireType != Protobuf.WireType.Len) continue;
            var product = ParseInstall(field.Bytes.Span);
            if (product != null) products.Add(product);
        }
        return products;
    }

    private static BattleNetProduct? ParseInstall(ReadOnlySpan<byte> data)
    {
        string? uid = null, code = null, path = null, folder = null, version = null, family = null;
        List<Protobuf.Field> fields;
        try { fields = Protobuf.ReadMessage(data); }
        catch (FormatException) { return null; }

        foreach (var f in fields)
        {
            switch (f.Number)
            {
                case InstallUid when f.WireType == Protobuf.WireType.Len:
                    uid = f.AsString();
                    break;
                case InstallProductCode when f.WireType == Protobuf.WireType.Len:
                    code = f.AsString();
                    break;
                case InstallSettings when f.WireType == Protobuf.WireType.Len:
                    (path, folder) = ParseSettings(f.Bytes.Span);
                    break;
                case InstallFamily when f.WireType == Protobuf.WireType.Len:
                    family = f.AsString();
                    break;
                case InstallCachedState when f.WireType == Protobuf.WireType.Len:
                    version = ParseVersion(f.Bytes.Span);
                    break;
            }
        }
        return string.IsNullOrEmpty(uid) ? null : new BattleNetProduct(uid, code, NormalizePath(path), folder, version,
            string.IsNullOrEmpty(family) ? null : family);
    }

    private static (string? path, string? folder) ParseSettings(ReadOnlySpan<byte> data)
    {
        List<Protobuf.Field> fields;
        try { fields = Protobuf.ReadMessage(data); }
        catch (FormatException) { return (null, null); }

        string? path = null, folder = null;
        var folderShaped = new List<string>();
        foreach (var f in fields)
        {
            if (f.WireType != Protobuf.WireType.Len) continue;
            if (f.Number == SettingsInstallPath) path = f.AsString();
            else if (f.Number == SettingsGameSubfolder) folder = f.AsString();
            else if (f.TryAsString(out var s) && GameFolderPattern.IsMatch(s)) folderShaped.Add(s);
        }
        if (string.IsNullOrEmpty(folder) && folderShaped.Count == 1) folder = folderShaped[0];
        return (path, string.IsNullOrEmpty(folder) ? null : folder);
    }

    private static string? ParseVersion(ReadOnlySpan<byte> cached)
    {
        try
        {
            foreach (var c in Protobuf.ReadMessage(cached))
            {
                if (c.Number != CachedBaseState || c.WireType != Protobuf.WireType.Len) continue;
                foreach (var b in Protobuf.ReadMessage(c.Bytes.Span))
                    if (b.Number == BaseCurrentVersionStr && b.WireType == Protobuf.WireType.Len)
                        return b.AsString();
            }
        }
        catch (FormatException) { }
        return null;
    }

    /// <summary>"C:/Program Files (x86)/World of Warcraft/" → "C:\Program Files (x86)\World of Warcraft".</summary>
    internal static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : PathUtil.TrimEnd(path.Trim().Replace('/', '\\'));
}

/// <summary>Minimal protobuf wire-format reader (no schema, no dependencies).</summary>
public static class Protobuf
{
    public enum WireType { Varint = 0, I64 = 1, Len = 2, StartGroup = 3, EndGroup = 4, I32 = 5 }

    public readonly record struct Field(int Number, WireType WireType, ulong Value, ReadOnlyMemory<byte> Bytes)
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

        public string AsString() => Encoding.UTF8.GetString(Bytes.Span);

        /// <summary>True when the bytes are valid UTF-8 text without control characters.</summary>
        public bool TryAsString(out string value)
        {
            try
            {
                value = StrictUtf8.GetString(Bytes.Span);
                foreach (var ch in value)
                    if (char.IsControl(ch)) { value = ""; return false; }
                return true;
            }
            catch (DecoderFallbackException)
            {
                value = "";
                return false;
            }
        }
    }

    public static List<Field> ReadMessage(ReadOnlySpan<byte> data)
    {
        var fields = new List<Field>();
        int pos = 0;
        while (pos < data.Length)
        {
            ulong key = ReadVarint(data, ref pos);
            int number = (int)(key >> 3);
            var wire = (WireType)(key & 7);
            if (number <= 0) throw new FormatException("Invalid protobuf field number.");
            switch (wire)
            {
                case WireType.Varint:
                    fields.Add(new Field(number, wire, ReadVarint(data, ref pos), default));
                    break;
                case WireType.I64:
                    Need(data, pos, 8);
                    fields.Add(new Field(number, wire, BitConverter.ToUInt64(data.Slice(pos, 8)), default));
                    pos += 8;
                    break;
                case WireType.I32:
                    Need(data, pos, 4);
                    fields.Add(new Field(number, wire, BitConverter.ToUInt32(data.Slice(pos, 4)), default));
                    pos += 4;
                    break;
                case WireType.Len:
                    ulong len = ReadVarint(data, ref pos);
                    if (len > (ulong)(data.Length - pos)) throw new FormatException("Truncated protobuf field.");
                    fields.Add(new Field(number, wire, len, data.Slice(pos, (int)len).ToArray()));
                    pos += (int)len;
                    break;
                default:
                    throw new FormatException($"Unsupported protobuf wire type {(int)wire}.");
            }
        }
        return fields;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (pos >= data.Length) throw new FormatException("Truncated protobuf varint.");
            byte b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new FormatException("Protobuf varint too long.");
    }

    private static void Need(ReadOnlySpan<byte> data, int pos, int count)
    {
        if (data.Length - pos < count) throw new FormatException("Truncated protobuf field.");
    }
}
