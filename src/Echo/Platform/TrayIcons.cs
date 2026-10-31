using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Echo.Platform;

internal enum TrayState
{
    /// <summary>Teams is not running.</summary>
    Idle,

    /// <summary>Teams is running, waiting for a call.</summary>
    Ready,

    /// <summary>A call is being recorded.</summary>
    Recording,

    /// <summary>Transcription / summary in progress.</summary>
    Processing,
}

/// <summary>Notification-area icons, drawn at runtime so they stay sharp at any DPI.</summary>
internal static class TrayIcons
{
    private static readonly Dictionary<TrayState, Icon> Cache = [];

    public static Icon AppIcon { get; } = LoadAppIcon();

    public static Icon For(TrayState state)
    {
        if (Cache.TryGetValue(state, out var icon)) return icon;
        icon = Draw(state);
        Cache[state] = icon;
        return icon;
    }

    private static Icon LoadAppIcon()
    {
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream("echo.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    private static Icon Draw(TrayState state)
    {
        int size = SystemInformation.SmallIconSize.Width * 2;
        using var logo = new Icon(AppIcon, size, size).ToBitmap();
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            if (state == TrayState.Idle)
            {
                // No calling application open: the logo in grey.
                var gray = new System.Drawing.Imaging.ColorMatrix(
                [
                    [0.30f, 0.30f, 0.30f, 0, 0],
                    [0.59f, 0.59f, 0.59f, 0, 0],
                    [0.11f, 0.11f, 0.11f, 0, 0],
                    [0, 0, 0, 1, 0],
                    [0, 0, 0, 0, 1],
                ]);
                using var attributes = new System.Drawing.Imaging.ImageAttributes();
                attributes.SetColorMatrix(gray);
                g.DrawImage(logo, new Rectangle(0, 0, size, size), 0, 0, logo.Width, logo.Height, GraphicsUnit.Pixel, attributes);
            }
            else
            {
                g.DrawImage(logo, 0, 0, size, size);
            }

            Color? badge = state switch
            {
                TrayState.Recording => Color.FromArgb(255, 77, 94),
                TrayState.Processing => Color.FromArgb(255, 181, 71),
                _ => null,
            };
            if (badge is { } c)
            {
                float r = size * 0.46f;
                using var ring = new SolidBrush(Color.White);
                using var dot = new SolidBrush(c);
                g.FillEllipse(ring, size - r - 1, size - r - 1, r + 1, r + 1);
                g.FillEllipse(dot, size - r + 1.5f, size - r + 1.5f, r - 4, r - 4);
            }
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
