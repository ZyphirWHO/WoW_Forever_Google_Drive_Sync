using System.Text;

namespace WoWSync.Core.Tests.Fakes;

/// <summary>
/// product.db fixtures. Laptop() is the real file from the laptop, byte for byte, except the
/// account/geo country strings, replaced with same-length "ZZZ"/"ZZ". The builders below make
/// synthetic files with the same (verified) field numbers for other scenarios.
/// </summary>
public static class ProductDbFixtures
{
    public static byte[] Laptop() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "laptop-product.db"));

    public static byte[] Database(params byte[][] products)
    {
        var w = new Writer();
        foreach (var p in products) w.Bytes(1, p);              // repeated ProductInstall product_installs = 1
        w.Bytes(2, new Writer().String(1, "wowf").ToArray());   // active_installs: noise the reader must skip
        w.Bytes(4, new Writer().String(1, "wow").Varint(2, 1).ToArray());
        return w.ToArray();
    }

    public static byte[] Product(string uid, string? installPath, string? folder, string? version,
        bool folderFirst = false, int folderField = 13)
    {
        var settings = new Writer();
        if (folderFirst && folder != null) settings.String(folderField, folder);
        if (installPath != null) settings.String(1, installPath);   // install_path = 1
        settings.String(2, "enUS")                                  // play_region / language noise
                .Varint(3, 1)
                .String(6, "enUS")
                .String(7, "enUS");
        if (!folderFirst && folder != null) settings.String(folderField, folder); // game_subfolder = 13

        var baseState = new Writer().Varint(1, 1).Varint(2, 1);
        if (version != null) baseState.String(7, version);          // current_version_str = 7
        var cached = new Writer().Bytes(1, baseState.ToArray());    // base_product_state = 1

        return new Writer()
            .String(1, uid)                                          // uid = 1
            .String(2, uid)                                          // product_code = 2
            .Bytes(3, settings.ToArray())                            // settings = 3
            .Bytes(4, cached.ToArray())                              // cached_product_state = 4
            .ToArray();
    }

    private sealed class Writer
    {
        private readonly MemoryStream _ms = new();

        public Writer Varint(int field, ulong value) { Key(field, 0); WriteVarint(value); return this; }
        public Writer String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public Writer Bytes(int field, byte[] value)
        {
            Key(field, 2);
            WriteVarint((ulong)value.Length);
            _ms.Write(value);
            return this;
        }

        public byte[] ToArray() => _ms.ToArray();

        private void Key(int field, int wire) => WriteVarint((ulong)((field << 3) | wire));

        private void WriteVarint(ulong v)
        {
            while (v >= 0x80) { _ms.WriteByte((byte)(v | 0x80)); v >>= 7; }
            _ms.WriteByte((byte)v);
        }
    }
}
