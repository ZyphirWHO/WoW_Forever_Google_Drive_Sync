using InterfaceSync.Core.Localization;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Core;

/// <summary>How a state should look: the colour family both apps use for dots, pills and the headline icon.</summary>
public enum StatusTone { Good, Busy, Waiting, Paused, Attention }

/// <summary>Plain words and tones for the status windows, shared by Windows and Mac.</summary>
public static class StatusWords
{
    public static StatusTone Tone(OverallState s) => s switch
    {
        OverallState.InSync => StatusTone.Good,
        OverallState.Syncing => StatusTone.Busy,
        OverallState.Paused => StatusTone.Paused,
        OverallState.NeedsAttention => StatusTone.Attention,
        _ => StatusTone.Waiting,
    };

    public static StatusTone Tone(SyncStatus s) => s switch
    {
        SyncStatus.Idle => StatusTone.Good,
        SyncStatus.Syncing => StatusTone.Busy,
        SyncStatus.Paused => StatusTone.Paused,
        SyncStatus.NeedsAttention or SyncStatus.Error => StatusTone.Attention,
        _ => StatusTone.Waiting,
    };

    /// <summary>The big line at the top of the status window.</summary>
    public static string Headline(OverallState s) => s switch
    {
        OverallState.InSync => L.T("Everything is in sync"),
        OverallState.Syncing => L.T("Syncing your settings…"),
        OverallState.Paused => L.T("Syncing is paused"),
        OverallState.NeedsAttention => L.T("Needs your attention"),
        _ => L.T("Waiting"),
    };

    /// <summary>"just now", "5 minutes ago", "3 hours ago", "2 days ago", then the date.</summary>
    public static string Ago(DateTime? utc, DateTime nowUtc)
    {
        if (utc is not { } t) return L.T("never");
        var d = nowUtc - t;
        if (d < TimeSpan.FromMinutes(1)) return L.T("just now");
        if (d < TimeSpan.FromHours(1)) return L.N((int)d.TotalMinutes, "{0} minute ago", "{0} minutes ago");
        if (d < TimeSpan.FromDays(1)) return L.N((int)d.TotalHours, "{0} hour ago", "{0} hours ago");
        if (d < TimeSpan.FromDays(7)) return L.N((int)d.TotalDays, "{0} day ago", "{0} days ago");
        return t.ToLocalTime().ToString("d MMM yyyy", L.Culture);
    }

    /// <summary>Last time anything was uploaded or downloaded for any version.</summary>
    public static DateTime? LastSynced(IEnumerable<VersionSync> workers) =>
        workers.SelectMany(w => new[] { w.State.LastPushUtc, w.State.LastPullUtc }).Where(t => t != null).Max();

    /// <summary>Under each version: "Uploaded 5 minutes ago · Downloaded 2 days ago".</summary>
    public static string Details(VersionSync w, DateTime nowUtc)
    {
        var s = w.State;
        if (s.LastPushUtc == null && s.LastPullUtc == null) return L.T("Not synced yet");
        return L.F("Uploaded {0} · Downloaded {1}", Ago(s.LastPushUtc, nowUtc), Ago(s.LastPullUtc, nowUtc));
    }

    /// <summary>"4:30 PM", "tomorrow 6:00 AM" or "Mon 6:00 AM" for when a timed pause ends.</summary>
    public static string Until(DateTime untilUtc, DateTime nowUtc)
    {
        var t = untilUtc.ToLocalTime();
        var today = nowUtc.ToLocalTime().Date;
        var time = t.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        if (t.Date == today) return time;
        if (t.Date == today.AddDays(1)) return L.F("tomorrow {0}", time);
        return $"{t.ToString("ddd", L.Culture)} {time}";
    }

    /// <summary>The pause choices in the tray and status window: label and when the pause ends (null: until resumed).</summary>
    public static IReadOnlyList<(string Label, DateTime? UntilUtc)> PauseChoices(DateTime nowUtc) =>
    [
        (L.T("For 1 hour"), nowUtc.AddHours(1)),
        (L.T("For 4 hours"), nowUtc.AddHours(4)),
        (L.T("Until tomorrow morning"), TomorrowMorning(nowUtc)),
        (L.T("Until I resume"), null),
    ];

    /// <summary>The next 6:00 AM local time at least an hour away.</summary>
    public static DateTime TomorrowMorning(DateTime nowUtc)
    {
        var local = nowUtc.ToLocalTime();
        var target = local.Date.AddHours(6);
        if (target <= local.AddHours(1)) target = target.AddDays(1);
        return DateTime.SpecifyKind(target, DateTimeKind.Local).ToUniversalTime();
    }

    /// <summary>Tray tooltip: "Interface Sync: in sync, synced 5 minutes ago" (at most 63 characters on Windows).</summary>
    public static string Tooltip(string stateText, DateTime? lastSyncedUtc, DateTime nowUtc)
    {
        var tip = $"{ProductInfo.Name}: {stateText}";
        if (lastSyncedUtc != null) tip += " · " + L.F("synced {0}", Ago(lastSyncedUtc, nowUtc));
        return tip.Length <= 63 ? tip : tip[..60] + "...";
    }

    /// <summary>"Windows 11 · version 3.1.0 · uploaded 5 minutes ago · retail, classic" under a computer's name.</summary>
    public static string ComputerDetail(DeviceInfo c, DateTime nowUtc)
    {
        var ago = Ago(c.LastSyncUtc, nowUtc);
        var action = c.LastAction switch
        {
            "Uploaded" => L.F("uploaded {0}", ago),
            "Downloaded" => L.F("downloaded {0}", ago),
            _ => L.F("synced {0}", ago),
        };
        var parts = new[] { c.System, c.AppVersion.Length > 0 ? L.F("version {0}", c.AppVersion) : "", action, string.Join(", ", c.Versions) };
        return string.Join(" · ", parts.Where(x => x.Length > 0));
    }

    /// <summary>"4 backups, 1 snapshot, 2 from game updates."</summary>
    public static string BackupCounts(IReadOnlyCollection<Backup.BackupInfo> list)
    {
        int regular = list.Count(b => !b.Pinned && !b.GameUpdate), snaps = list.Count(b => b.Pinned), updates = list.Count(b => b.GameUpdate);
        return $"{L.N(regular, "{0} backup", "{0} backups")}, {L.N(snaps, "{0} snapshot", "{0} snapshots")}, {L.N(updates, "{0} from a game update", "{0} from game updates")}.";
    }

    /// <summary>The overall state in the interface language (the service keeps it in English for the log).</summary>
    public static string StateText(SyncService service, DateTime nowUtc) =>
        service.State == OverallState.Paused && service.Settings.PausedUntilUtc is { } until
            ? L.F("Paused until {0}", Until(until, nowUtc))
            : L.T(service.StateText);

    /// <summary>A game version's state in the interface language.</summary>
    public static string StatusText(VersionSync w) => string.IsNullOrEmpty(w.StatusText) ? L.T(w.Status.ToString()) : L.T(w.StatusText);
}
