using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using InterfaceSync.Core;
using InterfaceSync.Core.Settings;
using Microsoft.Win32;
using P = InterfaceSync.Core.ThemePalette;

namespace InterfaceSync.App;

/// <summary>Colours for status text; each one follows the current theme.</summary>
internal enum TextRole { Normal, Muted, Warning, Error }

/// <summary>
/// Light or dark windows. Light is the normal Windows look. Dark is the warm, soft palette in
/// <see cref="ThemePalette.Dark"/>: dark title bar, dark scroll bars, flat buttons and a matching tray menu.
/// Windows' own message boxes stay in Windows' style.
/// </summary>
internal static class Theme
{
    public static AppTheme Mode { get; private set; } = AppTheme.System;
    public static bool IsDark { get; private set; }

    /// <summary>Raised on the UI thread after the colours change, so windows can redraw status colours.</summary>
    public static event Action? Changed;

    private static readonly ConditionalWeakTable<Control, object> Hooked = new();
    private static readonly ConditionalWeakTable<Control, Box<TextRole>> Roles = new();
    private sealed class Box<T>(T value) { public T Value = value; }

    public static Color Window => C(P.Dark.Window);
    public static Color Surface => C(P.Dark.Surface);
    public static Color Raised => C(P.Dark.Raised);
    public static Color Hover => C(P.Dark.Hover);
    public static Color Border => C(P.Dark.Border);
    public static Color Selection => C(P.Dark.Selection);
    public static Color Text => IsDark ? C(P.Dark.Text) : SystemColors.ControlText;
    public static Color Muted => IsDark ? C(P.Dark.Muted) : SystemColors.GrayText;
    public static Color Warning => C(IsDark ? P.Dark.Warning : P.Light.Warning);
    public static Color Error => C(IsDark ? P.Dark.Error : P.Light.Error);

    public static Color For(TextRole role) => role switch
    {
        TextRole.Muted => Muted,
        TextRole.Warning => Warning,
        TextRole.Error => Error,
        _ => Text,
    };

    private static Color C(string hex) { var (r, g, b) = P.Rgb(hex); return Color.FromArgb(r, g, b); }

    /// <summary>Sets the mode and recolours every open window.</summary>
    public static void Set(AppTheme mode)
    {
        Mode = mode;
        Refresh();
    }

    /// <summary>Re-reads Windows' setting (for "match Windows") and recolours every open window.</summary>
    public static void Refresh()
    {
        IsDark = Mode == AppTheme.Dark || (Mode == AppTheme.System && WindowsAppsUseDark());
        foreach (var f in Application.OpenForms.Cast<Form>().ToList()) Apply(f);
        Changed?.Invoke();
    }

    /// <summary>Settings → Personalisation → Colours → "Choose your app mode".</summary>
    internal static bool WindowsAppsUseDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Gives a label (or other control) a status colour that survives theme changes.</summary>
    public static void SetRole(Control c, TextRole role)
    {
        Roles.AddOrUpdate(c, new Box<TextRole>(role));
        c.ForeColor = For(role);
    }

    /// <summary>Colours a form and everything in it, now and for controls added later.</summary>
    public static void Apply(Form form)
    {
        if (Hooked.TryGetValue(form, out _) == false)
            form.HandleCreated += (_, _) => TitleBar(form);
        ApplyTree(form);
        if (form.IsHandleCreated) TitleBar(form);
    }

    private static void ApplyTree(Control c)
    {
        ApplyOne(c);
        if (!Hooked.TryGetValue(c, out _))
        {
            Hooked.Add(c, new object());
            c.ControlAdded += (_, e) => { if (e.Control != null) ApplyTree(e.Control); };
            if (NeedsNativeTheme(c)) c.HandleCreated += (_, _) => NativeTheme(c);
        }
        foreach (Control child in c.Controls) ApplyTree(child);
    }

    private static bool NeedsNativeTheme(Control c) => c is TextBoxBase or ListBox or ListView or ComboBox or ProgressBar;

    private static void ApplyOne(Control c)
    {
        bool dark = IsDark;
        switch (c)
        {
            case Form f:
                if (dark) { f.BackColor = Window; f.ForeColor = Text; }
                else { f.ResetBackColor(); f.ResetForeColor(); }
                break;

            case Button b:
                if (dark)
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.UseVisualStyleBackColor = false;
                    b.BackColor = Raised;
                    b.ForeColor = Text;
                    b.FlatAppearance.BorderColor = Border;
                    b.FlatAppearance.MouseOverBackColor = Hover;
                    b.FlatAppearance.MouseDownBackColor = Border;
                }
                else
                {
                    b.FlatStyle = FlatStyle.Standard;
                    b.ResetBackColor();
                    b.ResetForeColor();
                    b.UseVisualStyleBackColor = true;
                }
                break;

            case CheckBox or RadioButton:
                ((ButtonBase)c).FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                RoleOrReset(c);
                break;

            case TextBoxBase or ListBox:
                if (dark) { c.BackColor = Surface; c.ForeColor = Text; }
                else { c.ResetBackColor(); c.ResetForeColor(); }
                NativeTheme(c);
                break;

            case ComboBox cb:
                cb.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                if (dark) { cb.BackColor = Surface; cb.ForeColor = Text; }
                else { cb.ResetBackColor(); cb.ResetForeColor(); }
                NativeTheme(c);
                break;

            case ListView lv:
                HookListView(lv);
                lv.OwnerDraw = dark;   // dark column headers and selection need drawing ourselves
                if (dark) { lv.BackColor = Surface; lv.ForeColor = Text; }
                else { lv.ResetBackColor(); lv.ResetForeColor(); }
                NativeTheme(c);
                lv.Invalidate();
                break;

            case LinkLabel ll:
                ll.LinkColor = dark ? C(P.Dark.Accent) : Color.FromArgb(0, 102, 204);
                ll.ActiveLinkColor = ll.LinkColor;
                RoleOrReset(c);
                break;

            default:
                // Labels and layout panels take the form's colours, unless they carry a status colour.
                c.ResetBackColor();
                RoleOrReset(c);
                break;
        }
    }

    private static void RoleOrReset(Control c)
    {
        if (Roles.TryGetValue(c, out var role)) c.ForeColor = For(role.Value);
        else c.ResetForeColor();
    }

    // ------------------------------------------------------------------ list views

    private static readonly ConditionalWeakTable<ListView, object> HookedLists = new();

    private static void HookListView(ListView lv)
    {
        if (HookedLists.TryGetValue(lv, out _)) return;
        HookedLists.Add(lv, new object());
        lv.DrawColumnHeader += (_, e) =>
        {
            using var back = new SolidBrush(Raised);
            e.Graphics.FillRectangle(back, e.Bounds);
            using var line = new Pen(Border);
            e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var text = Rectangle.Inflate(e.Bounds, -6, 0);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, lv.Font, text, Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        lv.DrawItem += (_, e) =>
        {
            using var back = new SolidBrush(e.Item.Selected ? Selection : Surface);
            e.Graphics.FillRectangle(back, e.Bounds);
        };
        lv.DrawSubItem += (_, e) =>
        {
            if (e.Item == null || e.SubItem == null) return;
            var color = e.Item.ForeColor == SystemColors.WindowText || e.Item.ForeColor == SystemColors.ControlText ? Text : e.Item.ForeColor;
            var text = Rectangle.Inflate(e.Bounds, -6, 0);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, lv.Font, text, color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
    }

    // ------------------------------------------------------------------ tray menu

    public static void Apply(ToolStripDropDown menu)
    {
        menu.Renderer = IsDark ? new DarkMenuRenderer() : new ToolStripProfessionalRenderer();
        menu.ForeColor = IsDark ? Text : SystemColors.MenuText;
        menu.BackColor = IsDark ? Surface : SystemColors.Menu;
        foreach (var item in menu.Items.OfType<ToolStripMenuItem>())
        {
            item.ForeColor = menu.ForeColor;
            if (item.HasDropDownItems) Apply(item.DropDown);
        }
    }

    private sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new DarkMenuColors())
    {
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = Rectangle.Inflate(e.ImageRectangle, 2, 2);
            using var back = new SolidBrush(Selection);
            e.Graphics.FillRectangle(back, r);
            TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, r, C(P.Dark.Accent),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Muted;
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public DarkMenuColors() { UseSystemColors = false; }
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Raised;
        public override Color MenuItemPressedGradientEnd => Raised;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color CheckBackground => Selection;
        public override Color CheckSelectedBackground => Selection;
        public override Color CheckPressedBackground => Selection;
    }

    // ------------------------------------------------------------------ native parts (Windows 10 1809+; ignored elsewhere)

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private static void TitleBar(Form f)
    {
        if (!OperatingSystem.IsWindows() || !f.IsHandleCreated) return;
        try
        {
            int on = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)) != 0)   // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int));       // same, before Windows 10 20H1
            const uint NoMoveSizeZorderActivate_FrameChanged = 0x0002 | 0x0001 | 0x0004 | 0x0010 | 0x0020;
            SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0, NoMoveSizeZorderActivate_FrameChanged);
        }
        catch (Exception) { }   // older Windows: the title bar just stays light
    }

    /// <summary>Dark scroll bars and drop-downs for list and text controls.</summary>
    private static void NativeTheme(Control c)
    {
        if (!OperatingSystem.IsWindows() || !c.IsHandleCreated) return;
        try
        {
            var name = c is ComboBox ? (IsDark ? "DarkMode_CFD" : "CFD") : (IsDark ? "DarkMode_Explorer" : "Explorer");
            SetWindowTheme(c.Handle, name, null);
        }
        catch (Exception) { }
    }
}
