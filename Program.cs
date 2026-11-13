using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace FadeToSleep;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // --status <file>: write what the app sees right now and exit (handy for troubleshooting).
        if (args.Length == 2 && args[0] == "--status")
        {
            File.WriteAllText(args[1], Native.DescribeState());
            return;
        }

        using var mutex = new Mutex(true, @"Local\FadeToSleep.SingleInstance", out bool isNew);
        if (!isNew) return; // already running

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(startPreview: args.Contains("--preview")));
    }
}

/// <summary>Persisted user preferences (%APPDATA%\FadeToSleep\settings.json).</summary>
sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public int FadeSeconds { get; set; } = 90;

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FadeToSleep");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { /* non-fatal */ }
    }
}

/// <summary>The tray icon plus the idle-watching loop that drives the fade.</summary>
sealed class TrayApp : ApplicationContext
{
    const double PreviewFadeSeconds = 10;
    const double PreviewGraceSeconds = 1.0; // ignore the click that started the preview
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "FadeToSleep";

    readonly Settings _settings = Settings.Load();
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer = new();
    readonly ToolStripMenuItem _statusItem, _enabledItem, _startupItem, _fadeMenu;
    readonly List<Overlay> _overlays = new();

    bool _showing;
    bool _monitorOffSent;
    byte _lastAlpha;
    uint _lastTopmostTick;

    uint _timeoutSeconds;
    uint _lastTimeoutReadTick;
    bool _timeoutLoaded;

    // Preview state
    bool _previewActive;
    uint _previewStartTick;
    uint _previewInputBaseline;

    public TrayApp(bool startPreview)
    {
        _statusItem = new ToolStripMenuItem("Fade to Sleep") { Enabled = false };
        _enabledItem = new ToolStripMenuItem("Enabled", null, (_, _) => ToggleEnabled()) { Checked = _settings.Enabled };
        _fadeMenu = new ToolStripMenuItem("Fade length");
        foreach (var secs in new[] { 30, 60, 90, 120, 180 })
        {
            var item = new ToolStripMenuItem(FormatDuration(secs), null, (_, _) => SetFade(secs)) { Tag = secs };
            _fadeMenu.DropDownItems.Add(item);
        }
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
        menu.Items.Add(_fadeMenu);
        menu.Items.Add(new ToolStripMenuItem("Preview fade (10 sec)", null, (_, _) => StartPreview()));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));
        menu.Opening += (_, _) => RefreshMenu();

        _tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Fade to Sleep",
            ContextMenuStrip = menu,
            Visible = true,
        };

        SystemEvents.DisplaySettingsChanged += (_, _) => DisposeOverlays();
        SystemEvents.PowerModeChanged += (_, _) => _timeoutLoaded = false;

        _timer.Interval = 250;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        RefreshMenu();
        if (startPreview) StartPreview();
    }

    // ---------------------------------------------------------------- main loop

    void Tick()
    {
        double progress = ComputeProgress();

        if (progress < 0)
        {
            HideOverlays();
            _timer.Interval = 250;
            return;
        }

        _timer.Interval = 33; // ~30 fps while fading
        ShowOverlays(progress);

        if (progress >= 1 && !_monitorOffSent)
        {
            _monitorOffSent = true;
            if (_overlays.Count > 0) Native.TurnMonitorOff(_overlays[0].Handle);
        }
    }

    /// <summary>Returns fade progress 0..1, or -1 if the screen should be fully visible.</summary>
    double ComputeProgress()
    {
        uint now = Native.Now();

        if (_previewActive)
        {
            double sincePreview = unchecked(now - _previewStartTick) / 1000.0;
            uint lastInput = Native.LastInputTick();
            if (sincePreview < PreviewGraceSeconds)
            {
                _previewInputBaseline = lastInput;
                return -1;
            }
            if (lastInput != _previewInputBaseline)
            {
                _previewActive = false; // user moved: cancel
                return -1;
            }
            return Math.Min(1, (sincePreview - PreviewGraceSeconds) / PreviewFadeSeconds);
        }

        if (!_settings.Enabled) return -1;

        // Re-read Windows' "turn off display after" every few seconds so changes take effect.
        if (!_timeoutLoaded || unchecked(now - _lastTimeoutReadTick) > 5000)
        {
            _timeoutSeconds = Native.ReadDisplayTimeoutSeconds();
            _lastTimeoutReadTick = now;
            _timeoutLoaded = true;
        }
        if (_timeoutSeconds == 0) return -1; // display set to "never" turn off

        // If a video player / presentation / etc. is keeping the display on,
        // Windows won't sleep the monitor, so neither do we.
        if (!_monitorOffSent && Native.SomeoneNeedsDisplay()) return -1;

        double idle = Native.IdleSeconds();
        var (fade, start) = FadeWindow(_timeoutSeconds);
        if (idle < start) return -1;
        return Math.Min(1, (idle - start) / fade);
    }

    (double fade, double start) FadeWindow(uint timeout)
    {
        // Never let the fade eat more than 80% of the idle timeout.
        double fade = Math.Min(_settings.FadeSeconds, timeout * 0.8);
        return (fade, timeout - fade);
    }

    // ---------------------------------------------------------------- overlays

    void ShowOverlays(double progress)
    {
        if (_overlays.Count == 0)
            foreach (var screen in Screen.AllScreens)
                _overlays.Add(new Overlay(screen.Bounds));

        // Perceptual easing: brightness is perceived roughly as the cube root of
        // luminance, so this makes the dimming look even instead of "nothing, then sudden".
        double remaining = 1 - progress;
        byte alpha = (byte)Math.Round(255 * (1 - remaining * remaining * remaining));

        if (!_showing)
        {
            foreach (var o in _overlays) { o.SetAlpha(alpha); o.Show(); o.BringToTop(); }
            _showing = true;
            _lastAlpha = alpha;
            _lastTopmostTick = Native.Now();
            return;
        }

        if (alpha != _lastAlpha)
        {
            foreach (var o in _overlays) o.SetAlpha(alpha);
            _lastAlpha = alpha;
        }

        // Other always-on-top windows (taskbar, etc.) can creep above us; re-assert once a second.
        if (unchecked(Native.Now() - _lastTopmostTick) > 1000)
        {
            foreach (var o in _overlays) o.BringToTop();
            _lastTopmostTick = Native.Now();
        }
    }

    void HideOverlays()
    {
        _monitorOffSent = false;
        if (!_showing) return;
        foreach (var o in _overlays) { o.SetAlpha(0); o.Hide(); }
        _showing = false;
        _lastAlpha = 0;
    }

    void DisposeOverlays()
    {
        HideOverlays();
        foreach (var o in _overlays) o.Dispose();
        _overlays.Clear();
    }

    // ---------------------------------------------------------------- menu actions

    void StartPreview()
    {
        _previewActive = true;
        _previewStartTick = Native.Now();
        _previewInputBaseline = Native.LastInputTick();
        _tray.ShowBalloonTip(3000, "Fade to Sleep",
            "Previewing: hands off the mouse and keyboard. Move the mouse anytime to cancel.", ToolTipIcon.None);
    }

    void ToggleEnabled()
    {
        _settings.Enabled = !_settings.Enabled;
        _settings.Save();
        RefreshMenu();
    }

    void SetFade(int seconds)
    {
        _settings.FadeSeconds = seconds;
        _settings.Save();
        RefreshMenu();
    }

    void ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (IsStartupEnabled()) key.DeleteValue(RunValueName, false);
        else key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        RefreshMenu();
    }

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string s &&
               string.Equals(s.Trim('"'), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    void RefreshMenu()
    {
        uint timeout = Native.ReadDisplayTimeoutSeconds();
        string status;
        if (!_settings.Enabled) status = "Paused";
        else if (timeout == 0) status = "Display never sleeps — nothing to do";
        else
        {
            var (fade, start) = FadeWindow(timeout);
            status = $"Display sleeps after {FormatDuration((int)timeout)} · fade starts at {FormatDuration((int)Math.Round(start))}";
        }
        _statusItem.Text = status;
        _tray.Text = Truncate("Fade to Sleep — " + status, 127);

        _enabledItem.Checked = _settings.Enabled;
        _startupItem.Checked = IsStartupEnabled();
        foreach (ToolStripMenuItem item in _fadeMenu.DropDownItems)
            item.Checked = (int)item.Tag! == _settings.FadeSeconds;
    }

    void ExitApp()
    {
        _timer.Stop();
        DisposeOverlays();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }

    // ---------------------------------------------------------------- helpers

    static string FormatDuration(int seconds)
    {
        if (seconds < 60) return $"{seconds} sec";
        int m = seconds / 60, s = seconds % 60;
        if (s == 0) return m == 1 ? "1 min" : $"{m} min";
        return s == 30 ? $"{m}.5 min" : $"{m}m {s}s";
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    static Icon MakeIcon()
    {
        // A small circle fading from light to black: the app's whole idea in 32px.
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new LinearGradientBrush(new Rectangle(2, 2, 28, 28),
                Color.FromArgb(255, 255, 214, 120), Color.FromArgb(255, 10, 10, 20), LinearGradientMode.ForwardDiagonal);
            g.FillEllipse(brush, 2, 2, 28, 28);
            using var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 1.5f);
            g.DrawEllipse(pen, 2, 2, 28, 28);
        }
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        Native.DestroyIcon(h);
        return icon;
    }
}

/// <summary>A borderless, click-through, always-on-top black window covering one monitor.</summary>
sealed class Overlay : Form
{
    readonly Rectangle _bounds;

    public Overlay(Rectangle bounds)
    {
        _bounds = bounds;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Bounds = bounds;
        TopMost = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
                        | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetAlpha(0);
    }

    // Don't let WinForms resize us when the window lands on a monitor with a different DPI.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;
        Bounds = _bounds;
    }

    public void SetAlpha(byte alpha)
    {
        _ = Handle; // forces window creation (without showing it)
        Native.SetLayeredWindowAttributes(Handle, 0, alpha, Native.LWA_ALPHA);
    }

    public void BringToTop() =>
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
}

static class Native
{
    public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
                     WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    public const uint LWA_ALPHA = 0x2;
    public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    const int WM_SYSCOMMAND = 0x0112, SC_MONITORPOWER = 0xF170, MONITOR_OFF = 2;
    const int SystemExecutionState = 16;
    const uint ES_DISPLAY_REQUIRED = 0x2;

    static Guid GUID_VIDEO_SUBGROUP = new("7516b95f-f776-4464-8c53-06167f40cc99");
    static Guid GUID_VIDEO_POWERDOWN_TIMEOUT = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")] static extern uint GetTickCount();
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);
    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr inBuf, uint inLen, out uint outBuf, uint outLen);
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr activeScheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

    public static uint Now() => GetTickCount();

    public static uint LastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        GetLastInputInfo(ref info);
        return info.dwTime;
    }

    public static double IdleSeconds() => unchecked(Now() - LastInputTick()) / 1000.0;

    /// <summary>True if any app is asking Windows to keep the display on (video, presentations...).</summary>
    public static bool SomeoneNeedsDisplay()
    {
        uint state;
        return CallNtPowerInformation(SystemExecutionState, IntPtr.Zero, 0, out state, 4) == 0
               && (state & ES_DISPLAY_REQUIRED) != 0;
    }

    static bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    /// <summary>Windows' "Turn off my screen after" setting for the current power source, in seconds (0 = never).</summary>
    public static uint ReadDisplayTimeoutSeconds()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr schemePtr) != 0) return 0;
        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(schemePtr);
            uint value;
            uint err = OnBattery()
                ? PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref GUID_VIDEO_SUBGROUP, ref GUID_VIDEO_POWERDOWN_TIMEOUT, out value)
                : PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref GUID_VIDEO_SUBGROUP, ref GUID_VIDEO_POWERDOWN_TIMEOUT, out value);
            return err == 0 ? value : 0;
        }
        finally { LocalFree(schemePtr); }
    }

    public static void TurnMonitorOff(IntPtr anyOwnWindow) =>
        PostMessage(anyOwnWindow, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(MONITOR_OFF));

    public static string DescribeState() =>
        $"displayTimeoutSeconds={ReadDisplayTimeoutSeconds()}\n" +
        $"onBattery={OnBattery()}\n" +
        $"idleSeconds={IdleSeconds():F1}\n" +
        $"someoneNeedsDisplay={SomeoneNeedsDisplay()}\n" +
        $"screens={string.Join("; ", Screen.AllScreens.Select(s => $"{s.DeviceName} {s.Bounds}"))}\n";
}
