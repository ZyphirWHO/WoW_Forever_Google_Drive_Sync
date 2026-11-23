using System.Reflection;

namespace InterfaceSync.Core;

/// <summary>
/// The product name, read from this assembly's attributes, which come from Directory.Build.props
/// (ProductDisplayName / ProductId). Nothing else in the code spells the name out.
/// </summary>
public static class ProductInfo
{
    private static readonly Assembly Self = typeof(ProductInfo).Assembly;

    /// <summary>Display name, e.g. "Interface Sync": window titles, messages, Start menu.</summary>
    public static string Name { get; } =
        Self.GetCustomAttribute<AssemblyProductAttribute>()?.Product
        ?? throw new InvalidOperationException("AssemblyProduct is not set; check Directory.Build.props.");

    /// <summary>Identifier form, e.g. "InterfaceSync": exe, cloud folder, %LOCALAPPDATA% folder, startup entry, log prefix.</summary>
    public static string Id { get; } =
        Self.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "ProductId")?.Value
        ?? throw new InvalidOperationException("ProductId metadata is not set; check Directory.Build.props.");

    /// <summary>macOS bundle id, e.g. "io.github.zyphirwho.interfacesync": also the Login Item's name.</summary>
    public static string BundleId { get; } =
        Self.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "ProductBundleId")?.Value
        ?? throw new InvalidOperationException("ProductBundleId metadata is not set; check Directory.Build.props.");

    public static string Version => Self.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Descriptive use only: the game's name describes what the program works with.</summary>
    public const string Tagline = "Syncs your World of Warcraft® interface settings between your Windows PCs.";

    public const string NotAffiliated =
        "An independent, free, open-source tool. Not affiliated with, endorsed by or sponsored by Blizzard Entertainment, Inc.";

    /// <summary>Blizzard's credit line, as required for descriptive use of its marks.</summary>
    public const string TrademarkNotice =
        "World of Warcraft and Warcraft are trademarks or registered trademarks of Blizzard Entertainment, Inc., " +
        "in the U.S. and/or other countries.";

    public const string Licence = "MIT License";

    /// <summary>Names earlier builds used, so their data can be found and moved once.</summary>
    public static class Legacy
    {
        public static readonly string[] CloudFolderNames = ["WoWSync", "WoW Sync"];
        public const string LocalDataFolder = "WoWSync";
        public const string CommitFileName = "WTF-Account.wowsync.json";
        public const string TempSuffix = ".wowsync-tmp";
    }
}
