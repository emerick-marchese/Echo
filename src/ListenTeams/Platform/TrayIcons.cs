using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ListenTeams.Platform;

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
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream("listenteams.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    private static Icon Draw(TrayState state)
    {
        int size = SystemInformation.SmallIconSize.Width * 2;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Color background = state switch
            {
                TrayState.Idle => Color.FromArgb(120, 120, 128),
                _ => Color.FromArgb(18, 94, 110),
            };
            using (var brush = new SolidBrush(background))
            {
                g.FillEllipse(brush, 1, 1, size - 2, size - 2);
            }

            // Waveform
            float[] bars = [0.35f, 0.65f, 0.95f, 0.6f, 0.4f];
            float barWidth = size / 11f;
            float x = (size - (bars.Length * 2 - 1) * barWidth) / 2;
            using (var white = new SolidBrush(Color.White))
            {
                foreach (float h in bars)
                {
                    float height = h * size * 0.55f;
                    g.FillRectangle(white, x, (size - height) / 2, barWidth, height);
                    x += barWidth * 2;
                }
            }

            Color? badge = state switch
            {
                TrayState.Recording => Color.FromArgb(232, 62, 62),
                TrayState.Processing => Color.FromArgb(245, 166, 35),
                _ => null,
            };
            if (badge is { } c)
            {
                float r = size * 0.42f;
                using var ring = new SolidBrush(background);
                using var dot = new SolidBrush(c);
                g.FillEllipse(ring, size - r - 1, size - r - 1, r + 1, r + 1);
                g.FillEllipse(dot, size - r + 1, size - r + 1, r - 3, r - 3);
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
