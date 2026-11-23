using System.Security;
using InterfaceSync.Core.Abstractions;

namespace InterfaceSync.Core.Platform;

/// <summary>Starting Interface Sync when the player signs in: per user, no admin rights, no scripts.</summary>
public interface IStartAtLogin
{
    /// <summary>"Start with Windows" / "Open at Login".</summary>
    string Label { get; }

    /// <summary>Registered for this copy of the app and not switched off in the OS's own startup list.</summary>
    bool IsEnabled { get; }

    /// <summary>Why it can't be switched on from where the app runs now (e.g. from inside a zip), or null.</summary>
    string? CannotEnableReason { get; }

    void Enable();
    void Disable();

    /// <summary>If it's registered for an older location of this app, points it here. Returns what changed, or null.</summary>
    string? Repair();
}

/// <summary>
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run\InterfaceSync = "&lt;exe&gt;" --tray. Task Manager's
/// Startup apps page can switch it off (StartupApproved); switching it on here clears that.
/// </summary>
public sealed class WindowsStartAtLogin(IRegistry registry, string exePath, string tempFolder) : IStartAtLogin
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public static string ValueName => ProductInfo.Id;

    public string Label => "Start with Windows";
    private string Command => $"\"{exePath}\" --tray";

    public string? RegisteredPath => ExePathOf(registry.GetString(RegistryHive.CurrentUser, RunKey, ValueName));

    /// <summary>Task Manager stores 02.. (on) or 03.. (off) as binary; the low bit of the first byte means off.</summary>
    public bool SwitchedOffInTaskManager
    {
        get
        {
            var hex = registry.GetString(RegistryHive.CurrentUser, ApprovedKey, ValueName);
            return hex is { Length: >= 2 } && byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var b) && (b & 1) == 1;
        }
    }

    public bool IsEnabled => RegisteredPath is { } p && PathUtil.SamePath(p, exePath) && !SwitchedOffInTaskManager;

    public string? CannotEnableReason =>
        IsTemporary(exePath, tempFolder)
            ? $"{ProductInfo.Name} is running from a temporary folder (inside the zip), so it can't start from there next time. " +
              $"Copy {Path.GetFileName(exePath)} to a folder of its own, for example Documents\\{ProductInfo.Name}, and open it from there."
            : null;

    public void Enable()
    {
        if (CannotEnableReason is { } why) throw new InvalidOperationException(why);
        registry.SetString(RegistryHive.CurrentUser, RunKey, ValueName, Command);
        registry.DeleteValue(RegistryHive.CurrentUser, ApprovedKey, ValueName);   // undo a Task Manager "Disable"
    }

    public void Disable() => registry.DeleteValue(RegistryHive.CurrentUser, RunKey, ValueName);

    public string? Repair()
    {
        var old = RegisteredPath;
        if (old == null || PathUtil.SamePath(old, exePath) || CannotEnableReason != null) return null;
        registry.SetString(RegistryHive.CurrentUser, RunKey, ValueName, Command);
        return $"Start with Windows now opens {exePath} (it pointed to {old}).";
    }

    internal static string? ExePathOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    /// <summary>Opening the exe straight from a zip in Explorer runs it from %TEMP%\Temp1_….zip\.</summary>
    internal static bool IsTemporary(string exePath, string tempFolder) =>
        PathUtil.IsUnder(exePath, tempFolder) ||
        exePath.Contains(@"\Temp\Temp", StringComparison.OrdinalIgnoreCase) ||
        exePath.Contains(@"\INetCache\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// ~/Library/LaunchAgents/&lt;bundle id&gt;.plist with RunAtLoad: macOS lists it under Login Items
/// ("Allow in the background"), where the player can also switch it off.
/// </summary>
public sealed class MacStartAtLogin(IFileSystem fs, string home, string executablePath, string bundleId) : IStartAtLogin
{
    public string Label => "Open at Login";
    public string PlistPath => PathUtil.Join(home, "Library", "LaunchAgents", $"{bundleId}.plist");

    public string? RegisteredPath
    {
        get
        {
            if (!fs.FileExists(PlistPath)) return null;
            try
            {
                var text = fs.ReadAllText(PlistPath);
                var start = text.IndexOf("<array>", StringComparison.Ordinal);
                var s = text.IndexOf("<string>", start < 0 ? 0 : start, StringComparison.Ordinal);
                var e = s < 0 ? -1 : text.IndexOf("</string>", s, StringComparison.Ordinal);
                return s < 0 || e < 0 ? null : System.Net.WebUtility.HtmlDecode(text[(s + 8)..e]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    public bool IsEnabled => RegisteredPath == executablePath;

    public string? CannotEnableReason =>
        executablePath.Contains("/AppTranslocation/", StringComparison.Ordinal) || executablePath.StartsWith("/Volumes/", StringComparison.Ordinal)
            ? $"Move {ProductInfo.Name} to your Applications folder and open it from there first, so macOS can find it at login."
            : null;

    public void Enable()
    {
        if (CannotEnableReason is { } why) throw new InvalidOperationException(why);
        fs.CreateDirectory(PathUtil.Join(home, "Library", "LaunchAgents"));
        fs.WriteAllText(PlistPath, Plist(executablePath, bundleId));
    }

    public void Disable()
    {
        if (fs.FileExists(PlistPath)) fs.DeleteFile(PlistPath);
    }

    public string? Repair()
    {
        var old = RegisteredPath;
        if (old == null || old == executablePath || CannotEnableReason != null) return null;
        Enable();
        return $"Open at Login now opens {executablePath} (it pointed to {old}).";
    }

    internal static string Plist(string executable, string label) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{SecurityElement.Escape(label)}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{SecurityElement.Escape(executable)}</string>
            </array>
            <key>AssociatedBundleIdentifiers</key>
            <string>{SecurityElement.Escape(label)}</string>
            <key>RunAtLoad</key>
            <true/>
            <key>ProcessType</key>
            <string>Interactive</string>
            <key>LimitLoadToSessionType</key>
            <string>Aqua</string>
        </dict>
        </plist>

        """;
}
