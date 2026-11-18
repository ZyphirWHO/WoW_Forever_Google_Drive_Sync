using InterfaceSync.Core;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Backup;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Core.Tests.Fakes;

/// <summary>A temp folder holding a simulated cloud ("My Drive") shared by simulated PCs.</summary>
public sealed class SimWorld : IDisposable
{
    public SimWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "interfacesync-tests", Guid.NewGuid().ToString("N"));
        MyDrive = Path.Combine(Root, "cloud", "My Drive");
        Directory.CreateDirectory(MyDrive);
        Clock = new FakeClock(new DateTime(2026, 11, 18, 18, 0, 0, DateTimeKind.Utc));
    }

    public string Root { get; }
    public string MyDrive { get; }
    public FakeClock Clock { get; }

    public SimulatedPc Pc(string name, string versionFolder = "_retail_", IFileSystem? fs = null) =>
        new(this, name, versionFolder, fs ?? new PhysicalFileSystem());

    public string CloudAccount(string versionFolder = "_retail_") =>
        Path.Combine(MyDrive, CloudLayout.AppFolderName, versionFolder, CloudLayout.AccountFolderName);

    public string CommitFilePath(string versionFolder = "_retail_") =>
        Path.Combine(MyDrive, CloudLayout.AppFolderName, versionFolder, CloudLayout.CommitFileName);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

/// <summary>One PC: a WoW version folder, %LOCALAPPDATA%\InterfaceSync, a fake game process, and the engine.</summary>
public sealed class SimulatedPc
{
    private readonly SimWorld _world;
    private readonly string _versionFolder;

    public SimulatedPc(SimWorld world, string name, string versionFolder, IFileSystem fs)
    {
        _world = world;
        _versionFolder = versionFolder;
        Name = name;
        Fs = fs;
        var pcRoot = Path.Combine(world.Root, name);
        VersionPath = Path.Combine(pcRoot, "World of Warcraft", versionFolder);
        DataFolder = Path.Combine(pcRoot, "LocalAppData", "InterfaceSync");
        Directory.CreateDirectory(AccountPath);
        File.WriteAllText(Path.Combine(VersionPath, "Wow.exe"), "exe");
        File.WriteAllText(Path.Combine(VersionPath, "WTF", "Config.wtf"), $"SET gxWindow \"{name}\"");
        Restart();
    }

    public string Name { get; }
    public IFileSystem Fs { get; }
    public string VersionPath { get; }
    public string AccountPath => Path.Combine(VersionPath, "WTF", "Account");
    public string DataFolder { get; }
    public bool GameRunning { get; set; }
    public MemoryLog Log { get; private set; } = new();
    public VersionSync Sync { get; private set; } = null!;
    public BackupStore Backups { get; private set; } = null!;

    /// <summary>Optional hook called on every "is the game running?" check.</summary>
    public Func<bool>? RunningOverride { get; set; }

    /// <summary>Simulates the app starting (or the PC rebooting): fresh engine, state from disk.</summary>
    public void Restart()
    {
        Log = new MemoryLog();
        var version = new GameVersion(_versionFolder, VersionPath, ["Wow.exe"], true);
        Backups = new BackupStore(Fs, _world.Clock, Path.Combine(DataFolder, "Backups"));
        Sync = new VersionSync(
            version,
            new CloudLayout(_world.MyDrive, _versionFolder),
            Fs,
            _world.Clock,
            Backups,
            new StateStore(Fs, DataFolder),
            Log,
            Name,
            () => RunningOverride?.Invoke() ?? GameRunning);
    }

    /// <summary>Write a settings file as WoW would, stamped with the simulated clock.</summary>
    public void WriteSetting(string relative, string contents)
    {
        var path = Path.Combine(AccountPath, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, _world.Clock.UtcNow);
    }

    public string? ReadSetting(string relative)
    {
        var path = Path.Combine(AccountPath, relative.Replace('\\', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Launch, play (writing settings), quit, then poll through the after-close wait.</summary>
    public SyncOutcome PlaySession(params (string file, string contents)[] writes)
    {
        GameRunning = true;
        Sync.Tick();
        _world.Clock.Advance(TimeSpan.FromMinutes(30));
        foreach (var (file, contents) in writes) WriteSetting(file, contents);
        GameRunning = false;
        Sync.Tick();                                   // sees the exit
        _world.Clock.Advance(TimeSpan.FromSeconds(15));
        return Sync.Tick();                            // next poll pushes
    }

    /// <summary>A poll some minutes later (e.g. the other PC was used meanwhile).</summary>
    public SyncOutcome PollAfter(TimeSpan wait)
    {
        _world.Clock.Advance(wait);
        return Sync.Tick();
    }
}

/// <summary>Wraps the real file system and lets a test inject failures or attributes.</summary>
public sealed class ScriptedFileSystem : IFileSystem
{
    private readonly PhysicalFileSystem _inner = new();

    public Func<string, string, bool>? FailCopy { get; set; }
    public Action<string, string>? OnCopy { get; set; }
    public Func<string, bool>? OnlineOnly { get; set; }

    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
    public bool FileExists(string path) => _inner.FileExists(path);
    public bool IsReparsePoint(string path) => _inner.IsReparsePoint(path);
    public IEnumerable<string> EnumerateDirectories(string path) => _inner.EnumerateDirectories(path);
    public IEnumerable<string> EnumerateFiles(string path) => _inner.EnumerateFiles(path);

    public FileEntryInfo GetFileInfo(string path)
    {
        var info = _inner.GetFileInfo(path);
        return OnlineOnly?.Invoke(path) == true
            ? info with { Attributes = info.Attributes | FileEntryInfo.RecallOnDataAccess }
            : info;
    }

    public FileAttributes GetAttributes(string path) => _inner.GetAttributes(path);
    public void SetAttributes(string path, FileAttributes attributes) => _inner.SetAttributes(path, attributes);
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);

    public void CopyFile(string source, string destination)
    {
        if (FailCopy?.Invoke(source, destination) == true)
            throw new IOException($"Simulated failure copying {Path.GetFileName(source)}");
        _inner.CopyFile(source, destination);
        OnCopy?.Invoke(source, destination);
    }

    public void DeleteFile(string path) => _inner.DeleteFile(path);
    public void MoveFile(string source, string destination) => _inner.MoveFile(source, destination);
    public Func<string, bool>? FailDeleteDirectory { get; set; }

    public void DeleteDirectory(string path)
    {
        if (FailDeleteDirectory?.Invoke(path) == true)
            throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        _inner.DeleteDirectory(path);
    }
    public void RemoveLink(string path) => _inner.RemoveLink(path);
    public void MoveDirectory(string source, string destination) => _inner.MoveDirectory(source, destination);
    public string ReadAllText(string path) => _inner.ReadAllText(path);
    public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
    public void WriteAllText(string path, string contents) => _inner.WriteAllText(path, contents);
    public void AppendAllText(string path, string contents) => _inner.AppendAllText(path, contents);
    public IReadOnlyList<DriveEntry> GetDrives() => _inner.GetDrives();
}
