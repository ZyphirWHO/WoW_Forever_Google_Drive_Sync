using InterfaceSync.Core;
using InterfaceSync.Core.Sync;

using InterfaceSync.Core.Localization;

namespace InterfaceSync.App;

/// <summary>
/// One line in a list on a card: an icon, a headline and a muted detail line. Clickable (and reachable with Tab and
/// Enter) when something listens to <see cref="Activated"/>.
/// </summary>
internal sealed class InfoRow : Panel
{
    private readonly Glyph _icon;
    private readonly Label _headline;
    private readonly Label _detail;
    private Action? _activated;
    private bool _hover;

    public InfoRow(string glyph, string headline, string detail, TextRole iconRole = TextRole.Accent)
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Margin = new Padding(0, 2, 0, 2);
        Padding = new Padding(6, 6, 6, 6);
        _icon = new Glyph(glyph, 13f) { Location = new Point(6, 8), Margin = Padding.Empty };
        Theme.SetRole(_icon, iconRole);
        _headline = Ui.Text(headline);
        _headline.AutoSize = true;
        _detail = Ui.Text(detail, TextRole.Muted);
        Controls.AddRange([_icon, _headline, _detail]);
        AccessibleName = headline;
        AccessibleDescription = detail;
        AccessibleRole = AccessibleRole.ListItem;
        TabStop = false;
        foreach (Control c in Controls) { c.Click += (_, _) => _activated?.Invoke(); c.MouseEnter += (_, _) => SetHover(true); c.MouseLeave += (_, _) => SetHover(false); }
        Height = 48;
    }

    public string Headline => _headline.Text;

    /// <summary>Raised on click or Enter; makes the row a tab stop that looks clickable.</summary>
    public event Action? Activated
    {
        add
        {
            _activated += value;
            TabStop = true;
            Cursor = Cursors.Hand;
            foreach (Control c in Controls) c.Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleDescription = $"{_detail.Text}. {L.T("Press Enter to see the files.")}";
        }
        remove => _activated -= value;
    }

    private void SetHover(bool on)
    {
        if (_activated == null || _hover == on) return;
        _hover = on;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { SetHover(true); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { SetHover(false); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { Focus(); _activated?.Invoke(); base.OnClick(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space && _activated != null) { _activated(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Draw.Behind(this));
        if (_hover || Focused)
        {
            // An outline, not a fill: the labels keep the card's colour behind them.
            using var path = Draw.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 6 * Ui.Scale(this));
            using var pen = Focused && ShowFocusCues
                ? new Pen(Color.FromArgb(180, Theme.Accent)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot }
                : new Pen(Theme.Border);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>Lays the row out for a width: text wraps beside the icon.</summary>
    public void Fit(int width)
    {
        Width = width;
        int left = _icon.Left + _icon.PreferredSize.Width + Ui.Px(this, 10);
        int textWidth = Math.Max(100, width - left - Padding.Right);
        _headline.MaximumSize = new Size(textWidth, 0);
        _detail.MaximumSize = new Size(textWidth, 0);
        _headline.Location = new Point(left, Padding.Top);
        _detail.Location = new Point(left, _headline.Bottom + Ui.Px(this, 2));
        Height = _detail.Bottom + Padding.Bottom;
    }
}

/// <summary>Every recorded sync event, newest first, with the files each one changed.</summary>
internal sealed class ActivityDialog : Form
{
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill, AccessibleName = "Activity" };

    public ActivityDialog(IStatusHost app)
    {
        Text = $"{ProductInfo.Name}: {L.T("Activity")}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(860, 560);
        MinimumSize = new Size(560, 360);
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Icon = TrayIcons.AppIcon;

        var title = Ui.Text(L.T("Activity"), bold: true, size: 14f);
        title.Margin = new Padding(0, 0, 0, 4);
        var note = Ui.Text(L.T("What synced and when. Double-click an entry, or select it and press Enter, to see its files."), TextRole.Muted);
        note.Margin = new Padding(0, 0, 0, 10);
        _list.Columns.Add(L.T("When"), 190);
        _list.Columns.Add(L.T("Game"), 110);
        _list.Columns.Add(L.T("What"), 480);
        var now = DateTime.UtcNow;
        foreach (var e in app.Service.Activity.List())
            _list.Items.Add(new ListViewItem([e.Utc.ToLocalTime().ToString("ddd d MMM, HH:mm", L.Culture), e.Version, e.Headline]) { Tag = e, ToolTipText = StatusWords.Ago(e.Utc, now) });
        if (_list.Items.Count == 0) _list.Items.Add(new ListViewItem(["Nothing synced yet", "", ""]) { ForeColor = Theme.Muted });
        _list.DoubleClick += (_, _) => ShowSelected();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { ShowSelected(); e.Handled = true; } };

        var close = new ModernButton(L.T("Close"), ButtonKind.Primary) { DialogResult = DialogResult.OK, Margin = new Padding(8, 0, 0, 0) };
        var log = new ModernButton(L.T("Technical log"), ButtonKind.Ghost) { Icon = Draw.History };
        log.Click += (_, _) => AppActions.ViewLog(this, app);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([close, log]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(20, 16, 20, 16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(note, 0, 1);
        layout.Controls.Add(_list, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        AcceptButton = null;
        CancelButton = close;
        Theme.Apply(this);
        Ui.ScaleWithDisplay(this);
    }

    internal int EntryCount => _list.Items.Cast<ListViewItem>().Count(i => i.Tag is ActivityEntry);

    private void ShowSelected()
    {
        if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is ActivityEntry e) ShowDetails(this, e);
    }

    /// <summary>The files one event changed, grouped by what they are ("Details", "Key bindings"...).</summary>
    internal static string DetailsText(ActivityEntry e)
    {
        var lines = new List<string> { e.Headline, $"{e.Version} · {e.Utc.ToLocalTime().ToString("f", L.Culture)}", "" };
        foreach (var group in e.Files.Select(SettingsItem.Parse).GroupBy(i => i.Area, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(L.T(group.Key));
            foreach (var item in group) lines.Add($"    {item.RelativePath}{(item.CharacterLabel is { } c ? $"   ({c})" : "")}");
        }
        if (e.FileCount > e.Files.Count) lines.Add("…" + L.F("and {0} more", e.FileCount - e.Files.Count));
        return string.Join(Environment.NewLine, lines);
    }

    public static void ShowDetails(IWin32Window owner, ActivityEntry e)
    {
        if (ThemedDialog.AutoAnswer) return;
        using var d = new TextWindow(e.Headline, DetailsText(e), monospace: false);
        d.ShowDialog(owner);
    }
}
