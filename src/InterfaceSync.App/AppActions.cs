using InterfaceSync.Core;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.App;

/// <summary>Things both the status window and the settings window (and the tray menu) can do.</summary>
internal static class AppActions
{
    public static void ChangeCloudFolder(IWin32Window owner, IStatusHost app)
    {
        using var dialog = new ChangeCloudFolder(app.Service);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        var target = dialog.Chosen;
        var keep = dialog.KeepOnDevice;
        ProviderChangeResult? result = null;
        app.Run(
            () => result = app.Service.ChangeProvider(target, null, keep),
            () => ThemedDialog.Show(owner, result?.Message ?? "Nothing changed.", null, result?.Changed == true ? DialogKind.Info : DialogKind.Warning));
    }

    public static void OpenBackups(IWin32Window owner, IStatusHost app, VersionSync worker)
    {
        using var dialog = new RestoreDialog(app, worker);
        dialog.ShowDialog(owner);
    }

    public static void ChooseWhatSyncs(IWin32Window owner, IStatusHost app, VersionSync worker)
    {
        using var dialog = new SyncChoicesDialog(app.Service, worker);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        var rules = dialog.Rules;
        app.Run(() => app.Service.SetRules(worker, rules));
    }

    /// <summary>The pause choices: for 1 hour, 4 hours, until tomorrow morning, until resumed; or Resume.</summary>
    public static ContextMenuStrip PauseMenu(IStatusHost app)
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (app.Service.Settings.Paused)
                menu.Items.Add(new ToolStripMenuItem("Resume syncing", null, (_, _) => app.Run(() => app.Service.SetPaused(false))));
            else
                foreach (var (label, until) in StatusWords.PauseChoices(DateTime.UtcNow))
                    menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => app.Run(() => app.Service.SetPaused(true, until))));
            Theme.Apply(menu);
        };
        menu.Items.Add("…");   // replaced when it opens
        return menu;
    }

    public static void ShowPauseMenu(Control anchor, IStatusHost app)
    {
        if (app.Service.Settings.Paused)
        {
            app.Run(() => app.Service.SetPaused(false));
            return;
        }
        var menu = PauseMenu(app);
        menu.Closed += (_, _) => anchor.BeginInvoke(menu.Dispose);
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    public static void ReportProblem(IWin32Window? owner, IStatusHost app)
    {
        if (!ThemedDialog.Ask(owner,
                "This opens a new issue on GitHub with the diagnostics filled in: versions, what was found and the recent " +
                "log, with your folder paths, computer name and user name replaced by placeholders. Nothing is sent until " +
                "you check it, describe what happened and click Submit. You need a free GitHub account.",
                "Open GitHub", "Cancel", "Report a problem"))
            return;
        string text = "";
        app.Run(() => text = app.Service.RedactedDiagnostics(), () => Ui.OpenLink(Feedback.IssueUrl(text)));
    }

    public static void CopyDiagnostics(IWin32Window? owner, IStatusHost app)
    {
        Clipboard.SetText(app.Service.Diagnostics());
        ThemedDialog.Info(owner, "Paste them into your message or GitHub issue. They include your folder paths.", "Diagnostics copied");
    }

    public static void ShowWhatsNew(IWin32Window? owner, string? notes = null)
    {
        notes ??= ReleaseNotes.Current;
        if (notes == null) { Ui.OpenLink(Feedback.ReleasesPage); return; }
        if (ThemedDialog.AutoAnswer) return;
        using var d = new TextWindow($"What's new in {ProductInfo.Name} {ReleaseNotes.BaseVersion(ProductInfo.DisplayVersion)}",
            ReleaseNotes.ToPlainText(notes), monospace: false, link: ("All releases", Feedback.ReleasesPage));
        if (owner == null) d.StartPosition = FormStartPosition.CenterScreen;
        d.ShowDialog(owner);
    }

    public static void ViewLog(IWin32Window? owner, IStatusHost app)
    {
        var lines = app.Service.RecentLog(500).Reverse().Select(l => l.ToString());
        using var d = new TextWindow("Log (newest first)", string.Join(Environment.NewLine, lines), monospace: true,
            link: ("Open log folder", new Uri(app.Service.Paths.Logs)));
        d.ShowDialog(owner);
    }
}

/// <summary>A read-only text window: What's new, the log.</summary>
internal sealed class TextWindow : Form
{
    public TextWindow(string title, string text, bool monospace, (string Text, Uri Target)? link = null)
    {
        Text = $"{ProductInfo.Name}: {title}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(720, 560);
        MinimumSize = new Size(480, 320);
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Icon = TrayIcons.AppIcon;

        var heading = Ui.Text(title, bold: true, size: 14f);
        heading.Margin = new Padding(0, 0, 0, 10);
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = !monospace, Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None, Text = text, AccessibleName = title,
            Font = monospace ? new Font("Consolas", 9f) : new Font(Font.FontFamily, 10f),
        };
        if (monospace) box.ScrollBars = ScrollBars.Both;
        var close = new ModernButton("Close", ButtonKind.Primary) { DialogResult = DialogResult.OK, Margin = new Padding(8, 0, 0, 0) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(close);
        if (link is { } l)
        {
            var open = new ModernButton(l.Text, ButtonKind.Ghost);
            open.Click += (_, _) => { if (l.Target.IsFile) Ui.OpenFolder(this, l.Target.LocalPath); else Ui.OpenLink(l.Target); };
            buttons.Controls.Add(open);
        }
        var frame = new Card { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 6, 10), Margin = Padding.Empty };
        frame.Controls.Add(box);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(20, 16, 20, 16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(frame, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = CancelButton = close;
        Theme.Apply(this);
        Ui.ScaleWithDisplay(this);
        Shown += (_, _) => { box.SelectionStart = 0; box.SelectionLength = 0; close.Focus(); };
    }
}
