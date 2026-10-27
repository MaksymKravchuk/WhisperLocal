using System.Drawing.Drawing2D;
using WhisperLocal.Native;

namespace WhisperLocal.Ui;

/// <summary>
/// Tray icons drawn at runtime: an audio waveform (vertical bars) on a rounded square.
/// The background color shows the state.
/// </summary>
internal static class TrayIcons
{
    // Relative bar heights, slightly asymmetric so it reads as a waveform
    private static readonly float[] BarHeights = [0.4f, 0.75f, 1.0f, 0.6f, 0.35f];

    public static readonly Icon Idle = Make(Color.FromArgb(32, 33, 36), Color.White);
    public static readonly Icon Recording = Make(Color.FromArgb(214, 40, 40), Color.White);
    public static readonly Icon Processing = Make(Color.FromArgb(232, 140, 20), Color.White);
    public static readonly Icon Off = Make(Color.FromArgb(70, 70, 70), Color.FromArgb(150, 150, 150));

    private static Icon Make(Color background, Color bars)
    {
        // Draw at the native small-icon size of the current DPI to keep the bars crisp
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(background))
            using (var path = RoundedRect(new RectangleF(0, 0, size, size), size * 0.22f))
                g.FillPath(bg, path);

            // Pixel-snapped bars so they stay sharp at 16 px
            g.SmoothingMode = SmoothingMode.None;
            var barWidth = Math.Max(2, (int)Math.Round(size * 0.11));
            var gap = Math.Max(1, (int)Math.Round(size * 0.065));
            var totalWidth = BarHeights.Length * barWidth + (BarHeights.Length - 1) * gap;
            var x = (size - totalWidth) / 2;
            var maxHeight = size * 0.66f;

            using var brush = new SolidBrush(bars);
            foreach (var h in BarHeights)
            {
                var height = Math.Max(barWidth, (int)Math.Round(maxHeight * h));
                var y = (size - height) / 2;
                if (barWidth >= 3)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using var bar = RoundedRect(new RectangleF(x, y, barWidth, height), barWidth / 2f);
                    g.FillPath(brush, bar);
                    g.SmoothingMode = SmoothingMode.None;
                }
                else
                {
                    g.FillRectangle(brush, x, y, barWidth, height);
                }
                x += barWidth + gap;
            }
        }

        var handle = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
