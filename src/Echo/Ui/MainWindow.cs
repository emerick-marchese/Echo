using System.Runtime.InteropServices;
using Echo.Core;
using Echo.Core.Settings;
using Echo.Platform;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Echo.Ui;

/// <summary>
/// The Echo window: a WebView2 hosting the interface in ui/ (HTML/CSS/JS), connected to the
/// recording engine through <see cref="UiBridge"/>. Closing it keeps Echo running in the
/// notification area.
/// </summary>
internal sealed class MainWindow : Form
{
    private const string AppHost = "app.echo";
    private static readonly Color DarkBackground = Color.FromArgb(13, 14, 19);
    private static readonly Color LightBackground = Color.FromArgb(244, 245, 249);

    private readonly SettingsStore _settings;
    private readonly CallCoordinator _coordinator;
    private readonly UiBridge _bridge;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _liveTimer = new() { Interval = 250 };
    private bool _initialized;
    private bool _ready;
    private bool _wasRecording;
    private string? _mediaFolder;
    private DateTime _lastProgressPush;

    public MainWindow(SettingsStore settings, CallCoordinator coordinator)
    {
        _settings = settings;
        _coordinator = coordinator;
        _bridge = new UiBridge(settings, coordinator, this);
        _bridge.ThemeChanged += _ => ApplyTheme();
        _bridge.OutputFolderChanged += MapMediaFolder;

        Text = "Echo";
        Icon = TrayIcons.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1200, 800);
        MinimumSize = new Size(900, 620);
        Controls.Add(_web);
        ApplyTheme();

        _liveTimer.Tick += (_, _) => PushState();
        _coordinator.Processing.Completed += _ => BeginInvokeSafe(() => Push("recordingsChanged", null));
        _coordinator.Processing.Failed += (_, _) => BeginInvokeSafe(() => Push("recordingsChanged", null));
        _coordinator.Processing.ProgressChanged += _ => BeginInvokeSafe(PushProgress);
    }

    /// <summary>Set when the user really quits (tray menu): the window then closes for good.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    /// <summary>True when the window is shown and has the focus (notifications go to the window).</summary>
    public bool IsActiveWindow => Visible && WindowState != FormWindowState.Minimized && ContainsFocus;

    public void ShowAndActivate(string? page = null)
    {
        if (page is not null)
        {
            _bridge.StartPage = page;
            if (_ready) Push("navigate", new { page });
        }
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    public void Notify(UserNotification notification)
    {
        string level = notification.Icon switch
        {
            ToolTipIcon.Error => "error",
            ToolTipIcon.Warning => "warning",
            _ => "info",
        };
        Push("notify", new { title = notification.Title, text = notification.Text, level });
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_initialized) return;
        _initialized = true;
        await InitializeWebViewAsync();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _liveTimer.Enabled = Visible;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            // Keep recording in the background: closing only hides the window.
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBar();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(AppPaths.DataFolder, "WebView2"));
            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or FileNotFoundException)
        {
            Log.Error("WebView2 indisponible", ex);
            ShowRuntimeMissing();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.SetVirtualHostNameToFolderMapping(AppHost, Path.Combine(AppPaths.AppFolder, "ui"), CoreWebView2HostResourceAccessKind.Allow);
        MapMediaFolder(_settings.Current.OutputFolder);

        core.WebMessageReceived += async (_, args) =>
        {
            string reply = await _bridge.HandleAsync(args.WebMessageAsJson);
            core.PostWebMessageAsJson(reply);
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) AppPaths.Open(args.Uri);
        };
        core.NavigationStarting += (_, args) =>
        {
            // The interface never navigates away from itself.
            if (!args.Uri.StartsWith($"https://{AppHost}/", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
        };
        core.NavigationCompleted += (_, _) => _ready = true;
        core.Navigate($"https://{AppHost}/index.html");
    }

    private void MapMediaFolder(string folder)
    {
        var core = _web.CoreWebView2;
        if (core is null) return;
        try
        {
            Directory.CreateDirectory(folder);
            if (_mediaFolder is not null) core.ClearVirtualHostNameToFolderMapping(UiBridge.MediaHost);
            core.SetVirtualHostNameToFolderMapping(UiBridge.MediaHost, folder, CoreWebView2HostResourceAccessKind.Allow);
            _mediaFolder = folder;
        }
        catch (Exception ex)
        {
            Log.Warn($"Dossier des enregistrements inaccessible pour le lecteur : {ex.Message}");
        }
    }

    private void PushState()
    {
        if (!_ready) return;
        var status = _coordinator.Status;
        _liveTimer.Interval = status.Recording ? 250 : 1000;
        Push("state", _bridge.BuildState());
        if (_wasRecording != status.Recording)
        {
            _wasRecording = status.Recording;
            Push("recordingsChanged", null);
        }
    }

    /// <summary>Refreshes the recordings list with the processing progress, at most every 2 s.</summary>
    private void PushProgress()
    {
        if (DateTime.UtcNow - _lastProgressPush < TimeSpan.FromSeconds(2)) return;
        _lastProgressPush = DateTime.UtcNow;
        Push("recordingsChanged", null);
    }

    private void Push(string name, object? data)
    {
        if (!_ready || _web.CoreWebView2 is null) return;
        _web.CoreWebView2.PostWebMessageAsJson(UiBridge.Event(name, data));
    }

    private void BeginInvokeSafe(Action action)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
    }

    private bool IsDark()
    {
        string theme = _settings.Current.Theme;
        if (theme == "light") return false;
        if (theme == "dark") return true;
        // "system": Windows apps theme.
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private void ApplyTheme()
    {
        var background = IsDark() ? DarkBackground : LightBackground;
        BackColor = background;
        _web.DefaultBackgroundColor = background;
        ApplyTitleBar();
    }

    private void ApplyTitleBar()
    {
        if (!IsHandleCreated) return;
        int dark = IsDark() ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+ / Windows 11).
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    private void ShowRuntimeMissing()
    {
        Controls.Clear();
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(40),
            BackColor = BackColor,
        };
        var title = new Label
        {
            Text = "Le composant Microsoft Edge WebView2 est nécessaire pour afficher Echo.",
            AutoSize = true,
            ForeColor = IsDark() ? Color.White : Color.Black,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
        };
        var text = new Label
        {
            Text = "Il est présent sur Windows 11 et la plupart des PC Windows 10. Installez-le, puis relancez Echo. Les enregistrements continuent de fonctionner depuis l'icône près de l'horloge.",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = IsDark() ? Color.Gainsboro : Color.DimGray,
            Font = new Font("Segoe UI", 10f),
            Margin = new Padding(3, 10, 3, 16),
        };
        var link = new LinkLabel { Text = "Télécharger WebView2 (Microsoft)", AutoSize = true, Font = new Font("Segoe UI", 10f) };
        link.LinkClicked += (_, _) => AppPaths.Open("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
        panel.Controls.AddRange([title, text, link]);
        Controls.Add(panel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _liveTimer.Dispose();
            _web.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
