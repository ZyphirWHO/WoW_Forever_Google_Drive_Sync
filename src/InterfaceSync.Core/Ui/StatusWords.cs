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
        OverallState.InSync => "Everything is in sync",
        OverallState.Syncing => "Syncing your settings…",
        OverallState.Paused => "Syncing is paused",
        OverallState.NeedsAttention => "Needs your attention",
        _ => "Waiting",
    };

    /// <summary>"just now", "5 minutes ago", "3 hours ago", "2 days ago", then the date.</summary>
    public static string Ago(DateTime? utc, DateTime nowUtc)
    {
        if (utc is not { } t) return "never";
        var d = nowUtc - t;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return Plural((int)d.TotalMinutes, "minute");
        if (d < TimeSpan.FromDays(1)) return Plural((int)d.TotalHours, "hour");
        if (d < TimeSpan.FromDays(7)) return Plural((int)d.TotalDays, "day");
        return t.ToLocalTime().ToString("d MMM yyyy");
    }

    private static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";

    /// <summary>Last time anything was uploaded or downloaded for any version.</summary>
    public static DateTime? LastSynced(IEnumerable<VersionSync> workers) =>
        workers.SelectMany(w => new[] { w.State.LastPushUtc, w.State.LastPullUtc }).Where(t => t != null).Max();

    /// <summary>Under each version: "Uploaded 5 minutes ago · Downloaded 2 days ago".</summary>
    public static string Details(VersionSync w, DateTime nowUtc)
    {
        var s = w.State;
        if (s.LastPushUtc == null && s.LastPullUtc == null) return "Not synced yet";
        return $"Uploaded {Ago(s.LastPushUtc, nowUtc)} · Downloaded {Ago(s.LastPullUtc, nowUtc)}";
    }

    /// <summary>"4:30 PM", "tomorrow 6:00 AM" or "Mon 6:00 AM" for when a timed pause ends.</summary>
    public static string Until(DateTime untilUtc, DateTime nowUtc)
    {
        var t = untilUtc.ToLocalTime();
        var today = nowUtc.ToLocalTime().Date;
        var time = t.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        if (t.Date == today) return time;
        if (t.Date == today.AddDays(1)) return $"tomorrow {time}";
        return $"{t:ddd} {time}";
    }

    /// <summary>The pause choices in the tray and status window: label and when the pause ends (null: until resumed).</summary>
    public static IReadOnlyList<(string Label, DateTime? UntilUtc)> PauseChoices(DateTime nowUtc) =>
    [
        ("For 1 hour", nowUtc.AddHours(1)),
        ("For 4 hours", nowUtc.AddHours(4)),
        ("Until tomorrow morning", TomorrowMorning(nowUtc)),
        ("Until I resume", null),
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
        if (lastSyncedUtc != null) tip += $" · synced {Ago(lastSyncedUtc, nowUtc)}";
        return tip.Length <= 63 ? tip : tip[..60] + "...";
    }
}
