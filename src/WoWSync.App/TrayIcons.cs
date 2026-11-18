using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WoWSync.Core.Service;

namespace WoWSync.App;

/// <summary>Tray icons drawn at start-up: one colour per state, no image files to ship.</summary>
internal sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<OverallState, Icon> _icons = new();

    public TrayIcons()
    {
        _icons[OverallState.InSync] = Make(Color.FromArgb(46, 158, 68), filled: true);
        _icons[OverallState.Syncing] = Make(Color.FromArgb(47, 111, 222), filled: true);
        _icons[OverallState.Waiting] = Make(Color.FromArgb(138, 143, 152), filled: true);
        _icons[OverallState.NeedsAttention] = Make(Color.FromArgb(209, 52, 56), filled: true);
        _icons[OverallState.Paused] = Make(Color.FromArgb(138, 143, 152), filled: false);
    }

    public Icon For(OverallState state) => _icons[state];

    private static Icon Make(Color color, bool filled)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float inset = size * 0.08f;
            var rect = new RectangleF(inset, inset, size - 2 * inset - 1, size - 2 * inset - 1);
            if (filled)
            {
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, rect);
            }
            using var pen = new Pen(filled ? Color.FromArgb(200, Color.Black) : color, Math.Max(1.5f, size / 8f));
            g.DrawEllipse(pen, rect);
            using var font = new Font("Segoe UI", size * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(filled ? Color.White : color);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("W", font, text, new RectangleF(0, 0, size, size + 1), fmt);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values) icon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
