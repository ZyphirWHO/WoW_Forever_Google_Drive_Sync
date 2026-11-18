using WoWSync.Core;
using WoWSync.Core.Abstractions;

namespace WoWSync.Core.Tests.Fakes;

public sealed class FakeClock : IClock
{
    public FakeClock(DateTime start) => UtcNow = start;
    public DateTime UtcNow { get; set; }
    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

    private static string K(RegistryHive hive, string path) => $"{hive}\\{path.TrimEnd('\\')}";

    public FakeRegistry Set(RegistryHive hive, string keyPath, string valueName, string value)
    {
        SetString(hive, keyPath, valueName, value);
        return this;
    }

    public string? GetString(RegistryHive hive, string keyPath, string valueName) =>
        _keys.TryGetValue(K(hive, keyPath), out var values) && values.TryGetValue(valueName, out var v) ? v : null;

    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string keyPath)
    {
        var prefix = K(hive, keyPath) + "\\";
        return _keys.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetValueNames(RegistryHive hive, string keyPath) =>
        _keys.TryGetValue(K(hive, keyPath), out var values) ? values.Keys.ToList() : [];

    public void SetString(RegistryHive hive, string keyPath, string valueName, string value)
    {
        if (!_keys.TryGetValue(K(hive, keyPath), out var values))
            _keys[K(hive, keyPath)] = values = new(StringComparer.OrdinalIgnoreCase);
        values[valueName] = value;
    }

    public void DeleteValue(RegistryHive hive, string keyPath, string valueName)
    {
        if (_keys.TryGetValue(K(hive, keyPath), out var values)) values.Remove(valueName);
    }
}

public sealed class FakeEnvironment : IUserEnvironment
{
    public string LocalAppData { get; set; } = @"C:\Users\Player\AppData\Local";
    public string UserProfile { get; set; } = @"C:\Users\Player";
    public string UserName { get; set; } = "Player";
    public bool IsElevated { get; set; }
}

public sealed class FakeProcessList : IProcessList
{
    public List<ProcessEntry> Processes { get; } = [];

    public IReadOnlyList<ProcessEntry> GetProcesses(params string[] namePrefixes) =>
        Processes.Where(p => namePrefixes.Length == 0 ||
                             namePrefixes.Any(n => p.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase))).ToList();
}

/// <summary>
/// Windows-shaped in-memory file system for detection tests: case-insensitive, either separator.
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _links = new(StringComparer.OrdinalIgnoreCase);

    public List<DriveEntry> Drives { get; } = [];

    private static string N(string p) => PathUtil.TrimEnd(p.Replace('/', '\\'));

    public InMemoryFileSystem Dir(string path)
    {
        var p = N(path);
        while (p != null)
        {
            _dirs.Add(p);
            p = PathUtil.Parent(p) is { } parent ? N(parent) : null!;
        }
        return this;
    }

    public InMemoryFileSystem File(string path, string contents = "x")
    {
        var p = N(path);
        if (PathUtil.Parent(p) is { } parent) Dir(parent);
        _files[p] = contents;
        return this;
    }

    public InMemoryFileSystem Link(string path)
    {
        Dir(path);
        _links.Add(N(path));
        return this;
    }

    public InMemoryFileSystem RemoveTree(string path)
    {
        var p = N(path);
        _dirs.RemoveWhere(d => PathUtil.IsUnder(d, p));
        foreach (var f in _files.Keys.Where(f => PathUtil.IsUnder(f, p)).ToList()) _files.Remove(f);
        return this;
    }

    public bool DirectoryExists(string path) => _dirs.Contains(N(path));
    public bool FileExists(string path) => _files.ContainsKey(N(path));
    public bool IsReparsePoint(string path) => _links.Contains(N(path));

    private IEnumerable<string> Children(IEnumerable<string> set, string path)
    {
        var p = N(path);
        return set.Where(x => PathUtil.Parent(x) is { } parent && PathUtil.SamePath(parent, p)).ToList();
    }

    public IEnumerable<string> EnumerateDirectories(string path) => Children(_dirs, path);
    public IEnumerable<string> EnumerateFiles(string path) => Children(_files.Keys, path);

    public FileEntryInfo GetFileInfo(string path) =>
        new(N(path), _files[N(path)].Length, DateTime.UnixEpoch, FileAttributes.Normal);

    public void CreateDirectory(string path) => Dir(path);
    public void CopyFile(string source, string destination) => File(destination, _files[N(source)]);
    public void DeleteFile(string path) => _files.Remove(N(path));
    public void MoveFile(string source, string destination) { File(destination, _files[N(source)]); _files.Remove(N(source)); }
    public void DeleteDirectory(string path) => RemoveTree(path);
    public void RemoveLink(string path) { _links.Remove(N(path)); _dirs.Remove(N(path)); }
    public void MoveDirectory(string source, string destination) => throw new NotSupportedException();
    public string ReadAllText(string path) => _files[N(path)];
    public void WriteAllText(string path, string contents) => File(path, contents);
    public void AppendAllText(string path, string contents) => File(path, (FileExists(path) ? ReadAllText(path) : "") + contents);
    public IReadOnlyList<DriveEntry> GetDrives() => Drives;
}
