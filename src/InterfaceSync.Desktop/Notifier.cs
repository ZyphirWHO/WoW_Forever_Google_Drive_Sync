#if WINDOWS
using CommunityToolkit.WinUI.Notifications;
#endif

namespace InterfaceSync.Desktop;

/// <summary>What a notification's button does when clicked.</summary>
internal enum NoticeAction { Open, ChooseWhichToKeep, UpdateNow, ShowActivity }

/// <summary>
/// Pop-up notifications. Windows: native notifications (they stay in the notification centre) with buttons, e.g.
/// "Choose which to keep". macOS: unsigned apps can't post notifications, so the menu-bar panel shows them instead.
/// </summary>
internal sealed class Notifier
{
    /// <summary>Raised on the UI thread's behalf (callers marshal) when the player clicks a notification or a button.</summary>
    public event Action<NoticeAction>? Activated;

    public bool Supported => Os.IsWindows;

    public Notifier()
    {
#if WINDOWS
        try
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                var args = ToastArguments.Parse(e.Argument);
                var action = args.TryGetValue("action", out var a) && Enum.TryParse<NoticeAction>(a, out var parsed) ? parsed : NoticeAction.Open;
                Activated?.Invoke(action);
            };
        }
        catch (Exception) { }   // notifications are a nicety; the app works without them
#endif
    }

    /// <summary>Shows a notification. Returns false where it can't (macOS, or Windows refused).</summary>
    public bool Show(string title, string message, NoticeAction click = NoticeAction.Open, (string Label, NoticeAction Action)? button = null)
    {
#if WINDOWS
        try
        {
            var toast = new ToastContentBuilder()
                .AddArgument("action", click.ToString())
                .AddText(title)
                .AddText(message);
            if (button is { } b)
                toast.AddButton(new ToastButton().SetContent(b.Label).AddArgument("action", b.Action.ToString()));
            toast.Show();
            return true;
        }
        catch (Exception) { return false; }
#else
        _ = (title, message, click, button);
        return false;
#endif
    }
}
