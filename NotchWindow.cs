using System.Text;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace NotchHUD;

/// <summary>
/// The notch: a click-through, always-on-top shape hanging from the top edge
/// that changes form with what is happening. A small pill on the desktop, a
/// stats bar in a game, a brief pop-out for alerts and results, and a full
/// details panel on a hotkey.
/// </summary>
sealed partial class NotchWindow : Form
{
    enum View { Idle, Compact, Intro, Alert, Card, Panel, Boot, Wide }
    enum K { Pair, BigPair, Sep, Icon, Spark, Chip, Dot, Title, Text, Ring, Meter, Val, Small }

    struct Item
    {
        public K Kind;
        public string A, B;
        public Color Color;
        public Image? Img;
        public float V;
    }

    sealed class GameSession
    {
        public int Pid;
        public string Name = "", Exe = "";
        public string Key = "";       // history key: usually Exe, "Minecraft" for Java Minecraft (whose Exe is javaw)
        public bool Casual;           // tracked only because of always-on mode: no history, optimizer or pre-flight
        public Bitmap? Icon;
        public DateTime Start;
        public long StartTick;
        public Acc Acc = new();
        public Hist? LastHist;
        public SessionRecord? Previous;
        public Baseline? Usual;
        public volatile List<Check>? Checks;   // pre-flight results, filled in from a worker thread
        public long ChecksTick;
        public readonly List<float> Timeline = new(); // average FPS per bucket over the session
        public int BucketSec = 5, BucketN;
        public float BucketSum;
        public bool Benching;
        public readonly List<string> Events = new(); // what happened, for the session report

        /// <summary>Pre-flight lines revealed so far (one more every quarter second).</summary>
        public int ChecksShown(long now) =>
            Checks is { } c ? Math.Min(c.Count, (int)((now - Volatile.Read(ref ChecksTick)) / 250) + 1) : 0;

        public bool IntroActive(long now) =>
            Checks is { } c ? now < Volatile.Read(ref ChecksTick) + c.Count * 250 + 4000 : now < StartTick + 6000;
        public long BenchTick;
        public DateTime BenchStart;
        public Acc BenchAcc = new();
    }

    static string QuitFlag => Path.Combine(Log.Folder, "command.txt");
    static string NotifyFile => Path.Combine(Log.Folder, "notify.txt");

    readonly FrameTracker _frames = new();
    readonly Sensors _sensors = new();
    readonly Advisor _advisor = new();
    readonly HealthWatch _health = new();
    readonly WinEvents _events = new();
    readonly NetProbe _net = new();
    readonly Updater _updater = new();
    string _updatedNote = "";   // "Updated to vX" after an update restart, shown once the notch is up
    LiveForm? _liveForm;
    HealthForm? _healthForm;
    readonly Config _config = Config.Load();
    readonly float[] _scratch = new float[1 << 15];
    readonly float[] _recent = new float[600];
    readonly float[] _sorted = new float[600];
    int _recentN;
    float _recentMedian, _pace;

    readonly System.Windows.Forms.Timer _dataTimer = new() { Interval = 250 };
    readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 15 };
    readonly NotifyIcon _tray = new();
    readonly Bitmap _measureBmp = new(1, 1, PixelFormat.Format32bppPArgb);
    readonly Graphics _measure;
    readonly StringFormat _fmt;

    Font? _fLabel, _fValue, _fBig, _fTitle, _fText, _fChip, _fTiny, _fRing, _fHuge;
    float _fontScale;

    // state
    GameSession? _session;
    FrameStats _stats;
    bool _inGame, _shown = true, _panel;
    int _tick, _candPid, _candTicks;
    IntPtr _lastFgWnd;
    int _exclusiveSeen;
    readonly HashSet<int> _notGames = new();
    readonly HashSet<int> _knownGames = new();
    SessionRecord? _lastRecord;

    // transient views
    Alert? _alert;
    long _alertUntil, _cardUntil, _pulseUntil;
    List<Item>? _card;
    string _cardNote = "";
    int _prevStutters, _hogSeconds;
    bool _focused;
    string _lastTrack = "";
    readonly Optimizer _optimizer = new();
    const int PulseMs = 500, BootMs = 2600, TweenMs = 200;
    long _bootStart;
    float _fpsShown, _fpsFrom, _fpsTo;
    long _tweenStart;
    string _fontTheme = "";

    // layout and animation
    View _view = View.Idle;
    readonly List<Item> _row = new();
    readonly List<Item> _mid = new(), _right = new(); // centre and right groups of the wide bar
    float _w, _h, _tw, _th, _vw, _vh, _fade = 1;

    SettingsForm? _settings;
    HistoryForm? _history;

    public NotchWindow() : this(preview: false) { }

    NotchWindow(bool preview)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(0, 0);
        Size = new Size(1, 1);
        Text = "NotchHUD";

        Theme.Use(_config.Accent);
        _measure = Graphics.FromImage(_measureBmp);
        _measure.TextRenderingHint = TextRenderingHint.AntiAlias;
        _fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        _fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;

        _dataTimer.Tick += (_, _) => Tick();
        _animTimer.Tick += (_, _) => Animate();
        if (!preview) BuildTray();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
                      WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try { File.Delete(QuitFlag); } catch { }
        try
        {
            // Left behind by the update installer: say how it went, then clean up.
            string work = Path.Combine(Log.Folder, "update"), result = Path.Combine(work, "install.log");
            if (File.Exists(result))
            {
                string text = File.ReadAllText(result).Trim();
                _updatedNote = text.StartsWith("installed") ? "ok|" + text[10..] : "error|" + text.Split('\n')[0].Trim();
                Log.Write("update result: " + text);
            }
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
        catch { }
        _frames.Start();
        _sensors.Start();
        _events.Start();
        _net.Start();
        RegisterHotKey(Handle, 1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.F9);
        RegisterHotKey(Handle, 2, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.F10);
        RegisterHotKey(Handle, 3, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.F11);
        RegisterHotKey(Handle, 4, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.F8);
        _bootStart = Environment.TickCount64;
        Tick();
        _dataTimer.Start();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_HOTKEY = 0x0312;
        if (m.Msg == WM_HOTKEY)
        {
            switch ((int)m.WParam)
            {
                case 1: ToggleShown(); break;
                case 2: ToggleBenchmark(); break;
                case 3: TogglePanel(); break;
                case 4: ToggleWide(); break;
            }
            return;
        }
        base.WndProc(ref m);
    }

    // ================================================================ data

    void Tick()
    {
        try { TickCore(); }
        catch (Exception ex) { Log.Write("Tick failed: " + ex); }
    }

    void TickCore()
    {
        _tick++;
        bool second = _tick % 4 == 0;
        long now = Environment.TickCount64;

        // Other programs can pop a notice on the notch by appending lines to notify.txt:
        //   kind|title|detail      kind = ok, warn, error or info (anything else = info)
        // Only the newest line is shown; the file is deleted once read.
        if (File.Exists(NotifyFile))
        {
            try
            {
                string[] lines = File.ReadAllLines(NotifyFile);
                File.Delete(NotifyFile);
                string? last = lines.LastOrDefault(l => l.Contains('|'));
                if (last != null)
                {
                    string[] p = last.Split('|', 3);
                    string kind = p[0].Trim().ToLowerInvariant();
                    string title = Clip(p.Length > 1 ? p[1].Trim() : "", 40);
                    string detail = Clip(p.Length > 2 ? p[2].Trim() : "", 70);
                    Color c = kind switch
                    {
                        "ok" => Theme.Good,
                        "warn" => Theme.Warn,
                        "error" => Theme.Hot,
                        _ => Theme.Accent(_config.Accent),
                    };
                    if (title.Length > 0) ShowAlert(new Alert("ext", title, detail, c), 2600);
                }
            }
            catch (Exception ex) { Log.Write("notify failed: " + ex.Message); }
        }

        // A one-word command file lets scripts drive the notch: quit, panel, bench, hide.
        if (second && File.Exists(QuitFlag))
        {
            string cmd = "quit";
            try { cmd = File.ReadAllText(QuitFlag).Trim().ToLowerInvariant(); File.Delete(QuitFlag); } catch { }
            if (cmd == "panel") TogglePanel();
            else if (cmd == "bench") ToggleBenchmark();
            else if (cmd == "hide") ToggleShown();
            else if (cmd == "wide") ToggleWide();
            else if (cmd == "always") ToggleAlwaysOn();
            else if (cmd == "update") { if (_updater.Available) _updater.Install(this, Close); else _updater.CheckSoon(manual: true); }
            else if (cmd == "settings") { if (_settings is { IsDisposed: false }) _settings.Close(); else OpenSettings(); }
            else if (cmd == "history") { if (_history is { IsDisposed: false }) _history.Close(); else OpenHistory(); }
            else { Close(); return; }
        }

        if (_tick % 40 == 0 && _tick <= 1200)
            Log.Write($"view={_view} game={_inGame} fg={GetForegroundPid()} fps={_stats.Current:0} avg={_stats.Average:0} " +
                      $"1%={_stats.Low1:0} 0.1%={_stats.Low01:0} med={_stats.MedianMs:0.0}ms stut={_stats.Stutters10s} " +
                      $"cpu={_sensors.CpuTemp:0} gpu={_sensors.GpuTemp:0} load={_sensors.GpuLoad:0} " +
                      $"vram={_sensors.VramUsedMB:0}/{_sensors.VramTotalMB:0} cpuload={_sensors.CpuLoad:0}/{_sensors.CpuMaxThread:0} " +
                      $"hog={_sensors.HogName}:{_sensors.HogPct:0.0} tag={_advisor.Tag} tip={_advisor.Insights.FirstOrDefault()} events={_frames.EventsSeen} err={_frames.Error}");

        IntPtr fgWnd = GetForegroundWindow();
        GetWindowThreadProcessId(fgWnd, out uint fgRaw);
        int fg = (int)fgRaw;

        if (_session != null && second && !Alive(_session.Pid)) EndSession();
        // An app tracked only by always-on mode is dropped as soon as you leave it.
        if (_session is { Casual: true } && fg != _session.Pid && fg != Environment.ProcessId) EndSession();

        // The notch must stay above whatever just came to the front (borderless and fullscreen games too).
if (second && _config.FullscreenFix && _session != null && fg == _session.Pid) FixExclusiveFullscreen(fgWnd);

        if (fgWnd != _lastFgWnd)
        {
            _lastFgWnd = fgWnd;
            if (_shown) SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // A new game: something that keeps presenting frames in the foreground.
        // In always-on mode any app that is drawing frames counts.
        bool candidate = false, isGame = false;
        if (fg != 0 && fg != Environment.ProcessId && (_session == null || fg != _session.Pid)
            && _frames.TryGet(fg, _scratch, false, out var cand) && cand.AgeMs < 1500)
        {
            isGame = cand.FramesLastSec >= 20 && LooksLikeGame(fg, fgWnd);
            candidate = isGame || (_config.AlwaysOn && cand.FramesLastSec >= 5 && !IsShell(fg));
        }
        if (candidate)
        {
            if (_candPid == fg) _candTicks++;
            else { _candPid = fg; _candTicks = 1; }
            if (_candTicks >= (isGame ? 8 : 4))
            {
                if (_session != null) EndSession();
                StartSession(fg, fgWnd, casual: !isGame);
            }
        }
        else _candPid = 0;

        // Frames rendered while the game is in the background, and the hitch as
        // it comes back, say nothing about how it plays: leave them out.
        if (_session != null)
        {
            bool focused = fg == _session.Pid;
            if (focused != _focused)
            {
                _focused = focused;
                _frames.Mute(_session.Pid, focused ? now + 1500 : long.MaxValue);

                // Background apps get their normal speed back while you are
                // out of the game, and are calmed again when you return.
                if (_config.Optimizer)
                {
                    int gamePid = _session.Pid;
                    float ram = _sensors.RamPct;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { if (focused) _optimizer.Engage(gamePid, ram); else _optimizer.Release(); }
                        catch (Exception ex) { Log.Write("Optimizer failed: " + ex); }
                    });
                }
            }
        }

        _inGame = false;
        if (_session != null && fg == _session.Pid)
        {
            bool full = second || _stats.Average <= 0;
            if (_frames.TryGet(fg, _scratch, full, out var s) && s.AgeMs < 3000)
            {
                _inGame = true;
                if (full) _stats = s;
                else
                {
                    _stats.Current = s.Current;
                    _stats.FramesLastSec = s.FramesLastSec;
                    _stats.Jitter = s.Jitter;
                    _stats.Stutters10s = s.Stutters10s;
                    _stats.AgeMs = s.AgeMs;
                }
                _recentN = _frames.CopyRecent(fg, _recent, _panel || _config.Wide ? 600 : 240);
                if (_recentN > 0)
                {
                    Array.Copy(_recent, _sorted, _recentN);
                    Array.Sort(_sorted, 0, _recentN);
                    _recentMedian = _sorted[_recentN / 2];
                }
                // The FPS number counts to its new value instead of jumping.
                if (!_config.Animate || !_shown) _fpsShown = _fpsTo = _stats.Current;
                else if (Math.Abs(_stats.Current - _fpsTo) >= 0.5f)
                {
                    _fpsFrom = _fpsShown;
                    _fpsTo = _stats.Current;
                    _tweenStart = now;
                    _animTimer.Start();
                }
                // The edge of the notch flashes the instant a stutter lands.
                if (s.Stutters10s > _prevStutters && _shown && s.Phase is not { Length: > 0 })
                {
                    _pulseUntil = now + PulseMs;
                    _animTimer.Start();
                }
                _prevStutters = s.Stutters10s;
                // Short-term pace, so the FPS colour reacts to dips rather than to a new normal.
                if (second && s.Current > 0 && s.Phase is not { Length: > 0 }) _pace = _pace <= 0 ? s.Current : _pace + (s.Current - _pace) * 0.15f;
            }
        }
        _sensors.GamePid = _inGame ? _session!.Pid : 0;
        _frames.GpuLoadHint = _sensors.GpuLoad;
        _sensors.WatchMusic = _config.NowPlaying;

        if (second)
        {
            // A new track pops out briefly, the way a phone's island does.
            string track = _sensors.NowPlaying;
            if (track != _lastTrack)
            {
                _lastTrack = track;
                if (track.Length > 0 && _inGame && _alert == null && !_panel)
                    ShowAlert(new Alert("music", "Now playing", Clip(track, 60), Theme.Accent(_config.Accent)), 3500);
            }
        }

        if (_inGame && second)
        {
            var sess = _session!;
            sess.LastHist = _frames.GetSession(sess.Pid) ?? sess.LastHist;
            Alert? alert = _advisor.Update(_stats, _sensors, now - sess.StartTick, sess.LastHist);
            // A cutscene or menu says nothing about what limits the game.
            string phase = _stats.Phase ?? "";
            string tag = phase.Length > 0 ? "" : _advisor.Tag;
            sess.Acc.Add(_sensors, tag);
            if (sess.Benching) sess.BenchAcc.Add(_sensors, tag);

            // Scene changes go on the timeline when they end, with how long they lasted.
            if (phase != _lastPhase)
            {
                if (_lastPhase.Length > 0 && _lastPhaseSec >= 3)
                    Note(PhaseName(_lastPhase) + " ended", $"{_lastPhaseSec:0} s" + (_lastPhase == "cutscene" ? $" at {_lastPhaseFps:0} FPS" : "") + ", left out of the stats");
                _lastPhase = phase;
            }
            _lastPhaseSec = _stats.PhaseSec;
            _lastPhaseFps = _stats.PhaseFps;
            if (_config.NetWatch && _net.Net.Samples > 0) sess.Acc.AddPing(_net.LastMs);

            // Throttling starting and stopping goes on the session timeline even when no alert fires.
            string thr = _sensors.CpuThrottle.Length > 0 ? "CPU " + _sensors.CpuThrottle
                : _sensors.GpuThrottle.Length > 0 ? "GPU " + _sensors.GpuThrottle : "";
            if (thr != _lastThrottle)
            {
                if (thr.Length > 0) Note($"Throttling started ({thr})", $"CPU {_sensors.CpuTemp:0}° · GPU {_sensors.GpuTemp:0}°");
                else if (_lastThrottle.Length > 0) Note("Throttling stopped", "");
                _lastThrottle = thr;
            }

            // Session timeline: one bar per bucket; buckets double in length when the strip fills up.
            if (_stats.Current > 0)
            {
                sess.BucketSum += _stats.Current;
                if (++sess.BucketN >= sess.BucketSec)
                {
                    sess.Timeline.Add(sess.BucketSum / sess.BucketN);
                    sess.BucketSum = 0;
                    sess.BucketN = 0;
                    if (sess.Timeline.Count >= 240)
                    {
                        for (int i = 0; i < 120; i++) sess.Timeline[i] = (sess.Timeline[2 * i] + sess.Timeline[2 * i + 1]) / 2f;
                        sess.Timeline.RemoveRange(120, sess.Timeline.Count - 120);
                        sess.BucketSec *= 2;
                    }
                }
            }

            // Auto-boost: a background program that stays busy gets a lower priority.
            _hogSeconds = _config.Optimizer && _sensors.HogPct >= 8 ? _hogSeconds + 1 : 0;
            if (_hogSeconds >= 10)
            {
                _hogSeconds = 0;
                string hog = _sensors.HogName;
                if (_optimizer.Calm(_sensors.HogPid, hog))
                    alert = new Alert("boost", "Optimizer", $"{hog} was busy and is now in efficiency mode", Theme.Accent(_config.Accent));
            }

            if (alert != null) Note(alert.Title, alert.Detail);
            if (alert != null && _config.Alerts)
            {
                if (!_panel) ShowAlert(alert, 4500);
                if (_config.Voice) Voice.Say($"{alert.Title}. {alert.Detail}");
            }
        }

        // Throttling and failing hardware are worth knowing about in a game or out of one.
        if (second && _config.HealthWatch && _config.Alerts && _alert == null && !_panel && _health.Check(_sensors) is { } healthAlert)
        {
            ShowAlert(healthAlert, 4500);
            Note(healthAlert.Title, healthAlert.Detail);
            if (_config.Voice) Voice.Say($"{healthAlert.Title}. {healthAlert.Detail}");
        }

        // Lag matters most while playing, so the connection is only called out then.
        if (second && _inGame && _config.NetWatch && _config.Alerts && _alert == null && !_panel && _net.Check() is { } netAlert)
        {
            ShowAlert(netAlert, 5000);
            Note(netAlert.Title, netAlert.Detail);
            if (_config.Voice) Voice.Say($"{netAlert.Title}. {netAlert.Detail}");
        }

        // Important Windows errors. They wait in a queue while another card is up or the panel is open.
        if (second && _config.EventWatch && _config.Alerts && _alert == null && !_panel && _events.NextAlert(_session?.Exe) is { } winEvent)
        {
            string count = winEvent.Count > 1 ? $" (×{winEvent.Count})" : "";
            ShowAlert(new Alert("winevent", winEvent.Title + count, winEvent.Detail, winEvent.Severity >= 2 ? Theme.Hot : Theme.Warn), 6000);
            if (_config.Voice) Voice.Say($"{winEvent.Title}. {winEvent.Detail}");
        }

        // Updates: the result of the last install, then a GitHub check every 6 hours (first one 30 s after start).
        if (second && _updatedNote.Length > 0 && _tick > 16 && _alert == null && !_panel)
        {
            bool ok = _updatedNote.StartsWith("ok|");
            string detail = _updatedNote[(_updatedNote.IndexOf('|') + 1)..];
            ShowAlert(ok ? new Alert("update", $"Updated to {detail}", "NotchHUD " + Updater.CurrentText + " is running", Theme.Good)
                         : new Alert("update", "Update failed", Clip(detail, 70), Theme.Hot), 6000);
            _updatedNote = "";
        }
        if (second && _config.CheckUpdates && _tick >= 120) _updater.CheckSoon(manual: false);
        if (second && !_inGame && (_alert == null || _alert.Key == "update") && !_panel)
        {
            if (_updater.TakeAnnouncement())
                ShowAlert(new Alert("update", $"Update available: {_updater.LatestTag}",
                    $"You have {Updater.CurrentText}. Tray menu → Install update", Theme.Accent(_config.Accent)), 8000);
            else if (_updater.TakeUpToDate())
                ShowAlert(_updater.State.StartsWith("error")
                    ? new Alert("update", "Update failed", Clip(_updater.State[7..], 70), Theme.Warn)
                    : new Alert("update", "NotchHUD is up to date", "Version " + Updater.CurrentText, Theme.Good), 3500);
        }

        if (!_shown) return;
        UpdateLayout(now);
        Render();
        if (second) SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    static int GetForegroundPid()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return (int)pid;
    }

    static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }

    // ------------------------------------------------------ game detection

    static readonly HashSet<string> NotGameNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "chrome", "msedge", "firefox", "brave", "opera", "msedgewebview2", "mpv", "vlc",
        "obs64", "Code", "devenv", "WindowsTerminal", "claude", "Discord", "Spotify", "steam", "steamwebhelper",
        "EpicGamesLauncher", "RiotClientUx", "RiotClientServices", "Launcher", "LauncherPatcher", "SocialClubHelper",
        "ApplicationFrameHost", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "Taskmgr",
        "NZXT CAM", "FanControl", "TeamSpeak", "CurseForge", "Modrinth App", "Photos", "mspaint", "notepad",
        "cheatengine-x86_64-SSE4-AVX2", "SnailText", "MeetApp", "Battle.net", "GalaxyClient", "upc", "EADesktop",
        "MinecraftLauncher", "PrismLauncher", "MultiMC", "ATLauncher", "GDLauncher", "Lunar Client", "Feather Launcher",
    };

    static readonly string[] GameFolders =
    {
        @"\steamapps\", @"\Epic Games\", @"\Riot Games\", @"\Games\", @"\GOG Galaxy\Games\", @"\Ubisoft Game Launcher\games\",
        @"\EA Games\", @"\Rockstar Games\", @"\XboxGames\", @"\.minecraft\", @"\CurseForgeMC\", @"\Modrinth",
        @"\curseforge\minecraft\", @"\PrismLauncher\", @"\MultiMC\", @"\ATLauncher\", @"\gdlauncher", @"\.tlauncher\",
        @"\.lunarclient\", @"\.feather\", @"\Microsoft.4297127D64EC6_",
    };

    /// <summary>Windows shell surfaces that draw frames but are never what you want measured.</summary>
    static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "ApplicationFrameHost", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost",
        "TextInputHost", "LockApp", "ShellHost", "Widgets", "NotchHUD",
    };

    static bool IsShell(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return ShellNames.Contains(p.ProcessName);
        }
        catch { return true; }
    }

/// <summary>
    /// OpenGL and Vulkan games whose window exactly covers the screen (Minecraft's
    /// Fullscreen) get true exclusive fullscreen from the graphics driver, which
    /// hides every overlay, the notch included. Making the window one pixel taller
    /// keeps the game looking identical (the extra row is off-screen) but has it
    /// drawn through the desktop compositor, so the notch shows on top.
    /// DirectX games are left alone: they choose their own fullscreen mode.
    /// </summary>
    void FixExclusiveFullscreen(IntPtr wnd)
    {
        const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000;
        if (wnd == IntPtr.Zero || (GetWindowLong(wnd, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) { _exclusiveSeen = 0; return; }
        if (!GetWindowRect(wnd, out RECT r)) return;
        Rectangle m = Screen.FromHandle(wnd).Bounds;
        bool exact = r.Left == m.Left && r.Top == m.Top && r.Right == m.Right && r.Bottom == m.Bottom;
        if (!exact || !_frames.KernelOnly(_session!.Pid)) { _exclusiveSeen = 0; return; }
        if (++_exclusiveSeen < 2) return; // let a fullscreen switch settle first
        _exclusiveSeen = 0;
        if (SetWindowPos(wnd, IntPtr.Zero, m.Left, m.Top, m.Width, m.Height + 1, SWP_NOZORDER | SWP_NOACTIVATE))
            Log.Write($"fullscreen fix: {_session.Name} window {m.Width}x{m.Height} -> {m.Width}x{m.Height + 1} so the notch stays visible");
    }

    static bool IsJava(string processName) =>
        processName.Equals("javaw", StringComparison.OrdinalIgnoreCase) || processName.Equals("java", StringComparison.OrdinalIgnoreCase);

    static string WindowClass(IntPtr wnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(wnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    static string WindowTitle(IntPtr wnd)
    {
        var sb = new StringBuilder(512);
        return GetWindowText(wnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>"Minecraft* 1.21.4 - Singleplayer" → "Minecraft 1.21.4".</summary>
    static string CleanJavaTitle(string title)
    {
        title = title.Replace("*", "").Trim();
        int dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) title = title[..dash].Trim();
        return title;
    }

    /// <summary>The icon a window shows in the taskbar (Minecraft sets its grass block on the Java window).</summary>
    static Bitmap? WindowIcon(IntPtr wnd)
    {
        const int WM_GETICON = 0x7F, GCLP_HICON = -14;
        IntPtr h = SendMessage(wnd, WM_GETICON, (IntPtr)1, IntPtr.Zero);
        if (h == IntPtr.Zero) h = SendMessage(wnd, WM_GETICON, (IntPtr)2, IntPtr.Zero);
        if (h == IntPtr.Zero) h = GetClassLongPtr(wnd, GCLP_HICON);
        if (h == IntPtr.Zero) return null;
        try { using var icon = Icon.FromHandle(h); return icon.ToBitmap(); }
        catch { return null; }
    }

    bool LooksLikeGame(int pid, IntPtr wnd)
    {
        if (_notGames.Contains(pid)) return false;
        if (_knownGames.Contains(pid)) return true;
        if (_notGames.Count > 256) _notGames.Clear();
        if (_knownGames.Count > 256) _knownGames.Clear();

        string name, path = "";
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
            try { path = p.MainModule?.FileName ?? ""; } catch { }
        }
        catch { return false; }

        if (NotGameNames.Contains(name) || path.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase))
        {
            _notGames.Add(pid);
            return false;
        }
        foreach (string f in GameFolders)
            if (path.Contains(f, StringComparison.OrdinalIgnoreCase)) { _knownGames.Add(pid); return true; }

        // Minecraft and other Java games (LWJGL/GLFW windows) from any launcher, and Minecraft Bedrock.
        if ((IsJava(name) && WindowClass(wnd).StartsWith("GLFW", StringComparison.OrdinalIgnoreCase))
            || name.StartsWith("Minecraft.", StringComparison.OrdinalIgnoreCase))
        {
            _knownGames.Add(pid);
            return true;
        }

        // Anything else counts when it fills the screen.
        if (!GetWindowRect(wnd, out RECT r)) return false;
        Rectangle screen = Screen.FromHandle(wnd).Bounds;
        long area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
        return area >= (long)screen.Width * screen.Height * 8 / 10;
    }

    // ------------------------------------------------------------ sessions

    void StartSession(int pid, IntPtr wnd, bool casual)
    {
        var s = new GameSession { Pid = pid, Start = DateTime.Now, StartTick = Environment.TickCount64 };
        try
        {
            using var p = Process.GetProcessById(pid);
            s.Exe = p.ProcessName;
            s.Name = p.ProcessName;
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            if (path != null)
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                string title = (info.ProductName ?? "").Trim();
                if (title.Length < 2) title = (info.FileDescription ?? "").Trim();
                title = title.Replace("®", "").Replace("™", "").Replace("©", "").Replace("  ", " ").Trim();
                if (title.Length >= 2) s.Name = title;
                try
                {
                    using var icon = Icon.ExtractAssociatedIcon(path);
                    if (icon != null) s.Icon = icon.ToBitmap();
                }
                catch { }
            }
        }
        catch { }

        // Java games all run as javaw.exe ("OpenJDK Platform binary"): name them from the window instead.
        s.Key = s.Exe;
        if (IsJava(s.Exe))
        {
            string title = CleanJavaTitle(WindowTitle(wnd));
            bool minecraft = title.StartsWith("Minecraft", StringComparison.OrdinalIgnoreCase) || WindowClass(wnd).StartsWith("GLFW");
            s.Name = title.Length >= 2 ? title : minecraft ? "Minecraft" : "Java game";
            s.Key = minecraft ? "Minecraft" : "java:" + s.Name;
            if (WindowIcon(wnd) is { } windowIcon) { s.Icon?.Dispose(); s.Icon = windowIcon; }
        }
        else if (s.Exe.StartsWith("Minecraft.", StringComparison.OrdinalIgnoreCase))
        {
            s.Name = "Minecraft Bedrock";
            s.Key = "Minecraft Bedrock";
        }
        if (s.Name.Length > 34) s.Name = s.Name[..33].TrimEnd() + "…";

        s.Casual = casual;
        s.Previous = casual ? null : History.LastFor(s.Key, "session");
        s.Usual = casual ? null : History.BaselineFor(s.Key);
        _frames.BeginSession(pid);
        _advisor.Reset();
        _advisor.Base = s.Usual;
        _fpsShown = _fpsFrom = _fpsTo = 0;
        _stats = default;
        _pace = 0;
        _prevStutters = 0;
        _hogSeconds = 0;
        _focused = true;
        _session = s;
        _candPid = 0;

        // Optimizer and pre-flight scan run on a worker thread; the notch
        // reveals the results as they arrive.
        bool optimize = _config.Optimizer && !casual, scan = _config.Intro && !casual;
        float ramPct = _sensors.RamPct;
        if (optimize && !scan)
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { _optimizer.GameStarted(pid); _optimizer.Engage(pid, ramPct); }
                catch (Exception ex) { Log.Write("Optimizer failed: " + ex); }
            });
        if (scan)
        {
            bool voice = _config.Voice;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int optimized = -1;
                if (optimize)
                    try { _optimizer.GameStarted(pid); optimized = _optimizer.Engage(pid, ramPct); }
                    catch (Exception ex) { Log.Write("Optimizer failed: " + ex); }
                List<Check> checks;
                try { checks = Preflight.Run(_sensors, pid, s.Exe, optimized); }
                catch (Exception ex) { Log.Write("Pre-flight failed: " + ex); checks = new List<Check>(); }
                Volatile.Write(ref s.ChecksTick, Environment.TickCount64);
                s.Checks = checks;
                int bad = checks.Count(c => !c.Ok);
                Log.Write("pre-flight: " + string.Join(" | ", checks.Select(c => (c.Ok ? "ok " : "CHECK ") + c.Text)));
                if (voice) Voice.Say(bad == 0 ? "All systems go." : bad == 1 ? "One thing to check." : $"{bad} things to check.");
            });
        }
        else s.Checks = new List<Check>();
        Log.Write($"session start: {s.Name} ({s.Exe}, pid {pid}{(casual ? ", always-on" : "")})");
    }

    void EndSession()
    {
        var s = _session;
        if (s == null) return;
        if (s.Benching) FinishBenchmark(s, showCard: false);

        Hist? h = s.LastHist;
        string cause = _advisor.TopCause;
        _frames.EndSession(s.Pid);
        _optimizer.GameEnded();
        _session = null;
        _lastThrottle = _lastPhase = "";
        _inGame = false;
        _stats = default;
        _recentN = 0;
        _advisor.Reset();
        s.Icon?.Dispose();

        if (h == null || h.Seconds < 60 || s.Casual) return; // always-on apps are not games: keep them out of history
        var rec = MakeRecord("session", s, h, s.Acc, s.Start, cause);
        rec.Events = SessionEvents(s);
        SessionRecord? prev = s.Previous;
        History.Add(rec);
        _lastRecord = rec;
        Log.Write($"session end: {rec.Game} {rec.Seconds}s avg={rec.Avg:0} 1%={rec.Low1:0} 0.1%={rec.Low01:0} | {rec.Note}");
        if (_config.Summary) ShowCard(rec, prev, 12_000);
    }

    static SessionRecord MakeRecord(string kind, GameSession s, Hist h, Acc acc, DateTime start, string cause)
    {
        var r = new SessionRecord
        {
            Kind = kind,
            Game = s.Name,
            Exe = s.Key,
            Start = start,
            Seconds = (int)h.Seconds,
            Avg = h.AvgFps,
            Low1 = h.LowFps(0.01),
            Low01 = h.LowFps(0.001),
            Stutters = h.Stutters,
            Smoothness = Math.Max(0, Advisor.ScoreOf(h)),
            StutterCause = cause,
            MaxCpuTemp = acc.MaxCpu,
            MaxGpuTemp = acc.MaxGpu,
            AvgGpuLoad = acc.AvgGpuLoad,
            PeakVramMB = acc.PeakVram,
            Limit = acc.Limit,
            ThrottleSeconds = acc.ThrottleSeconds,
            ThrottleWhat = acc.ThrottleWhat,
            MaxCpuClockMHz = acc.MaxCpuClock,
            AvgPing = acc.AvgPing,
            MaxPing = acc.MaxPing,
            PingLoss = acc.PingLoss,
            LoadingSeconds = (int)(h.LoadingMs / 1000),
            CutsceneSeconds = (int)(h.CutsceneMs / 1000),
            MenuSeconds = (int)(h.MenuMs / 1000),
            Loads = h.Loads,
            Cutscenes = h.Cutscenes,
            Freezes = h.Freezes,
        };
        r.Note = Advisor.Debrief(r, s.Usual);
        return r;
    }

    void ToggleBenchmark()
    {
        var s = _session;
        if (s == null || !_inGame)
        {
            ShowAlert(new Alert("bench", "Benchmark", "Start a game first", Theme.Label), 2500);
            return;
        }
        if (s.Benching) { FinishBenchmark(s, showCard: true); return; }

        _frames.StartBench(s.Pid);
        s.Benching = true;
        s.BenchTick = Environment.TickCount64;
        s.BenchStart = DateTime.Now;
        s.BenchAcc = new Acc();
        _stats = default;
        ShowAlert(new Alert("bench", "Benchmark recording", "Ctrl+Alt+F10 to stop", Theme.Hot), 2500);
    }

    void FinishBenchmark(GameSession s, bool showCard)
    {
        s.Benching = false;
        Hist? h = _frames.StopBench(s.Pid);
        if (h == null || h.Count < 120)
        {
            if (showCard) ShowAlert(new Alert("bench", "Benchmark", "Too short to measure", Theme.Label), 2500);
            return;
        }
        var rec = MakeRecord("benchmark", s, h, s.BenchAcc, s.BenchStart, "");
        SessionRecord? prev = History.LastFor(s.Key, "benchmark");
        History.Add(rec);
        Log.Write($"benchmark: {rec.Game} {rec.Seconds}s avg={rec.Avg:0} 1%={rec.Low1:0} 0.1%={rec.Low01:0}");
        if (showCard) ShowCard(rec, prev, 14_000);
    }

    string _lastThrottle = "", _lastPhase = "";
    float _lastPhaseSec, _lastPhaseFps;

    static string PhaseName(string phase) => phase switch
    {
        "cutscene" => "Cutscene", "loading" => "Loading screen", "menu" => "Menu", _ => phase,
    };

    /// <summary>Adds a line to the running game's timeline: "m:ss  Title · detail".</summary>
    void Note(string title, string detail)
    {
        var s = _session;
        if (s == null || s.Events.Count >= 300) return;
        int sec = (int)((Environment.TickCount64 - s.StartTick) / 1000);
        string line = $"{sec / 60}:{sec % 60:00}  {title}" + (detail.Length > 0 ? " · " + detail : "");
        if (s.Events.Count == 0 || s.Events[^1] != line) s.Events.Add(line);
    }

    /// <summary>The session's own notes plus every Windows event logged while it ran, in time order.</summary>
    List<string> SessionEvents(GameSession s)
    {
        var all = new List<(int sec, string line)>();
        foreach (string e in s.Events)
        {
            int colon = e.IndexOf(':'), space = e.IndexOf(' ');
            int sec = colon > 0 && space > colon && int.TryParse(e[..colon], out int m) && int.TryParse(e[(colon + 1)..space], out int ss) ? m * 60 + ss : 0;
            all.Add((sec, e));
        }
        foreach (var w in _events.Recent)
        {
            if (w.Time < s.Start || w.Time > DateTime.Now) continue;
            int sec = (int)(w.Time - s.Start).TotalSeconds;
            all.Add((sec, $"{sec / 60}:{sec % 60:00}  Windows: {w.Title}{(w.Count > 1 ? $" (×{w.Count})" : "")} · {w.Detail}"));
        }
        return all.OrderBy(x => x.sec).Select(x => x.line).ToList();
    }

    void ShowAlert(Alert a, int ms)
    {
        _alert = a;
        _alertUntil = Environment.TickCount64 + ms;
        if (_config.Wide && !_panel) _fade = 0; // the wide bar keeps its shape, so flash the sweep instead
        if (_shown) { UpdateLayout(Environment.TickCount64); Render(); }
    }

    void ShowCard(SessionRecord rec, SessionRecord? prev, int ms)
    {
        Color accent = Theme.Accent(_config.Accent);
        var items = new List<Item>
        {
            new() { Kind = K.Chip, A = rec.Kind == "benchmark" ? "BENCHMARK" : "SESSION", Color = accent },
            new() { Kind = K.Title, A = rec.Game, Color = Theme.Ink },
            new() { Kind = K.Text, A = Duration(rec.Seconds), Color = Theme.Label },
            new() { Kind = K.Sep },
            new() { Kind = K.Pair, A = "AVG", B = Num(rec.Avg), Color = Theme.Ink },
            new() { Kind = K.Pair, A = "1%", B = Num(rec.Low1), Color = Theme.Ink },
            new() { Kind = K.Pair, A = "0.1%", B = Num(rec.Low01), Color = Theme.Ink },
            new() { Kind = K.Ring, A = rec.Smoothness.ToString(), V = rec.Smoothness, Color = ScoreColor(rec.Smoothness) },
        };
        _cardNote = rec.Note;
        if (prev != null && prev.Avg > 0)
        {
            float delta = (rec.Avg - prev.Avg) / prev.Avg * 100f;
            items.Add(new() { Kind = K.Sep });
            items.Add(new()
            {
                Kind = K.Pair, A = "VS LAST", B = (delta >= 0 ? "+" : "−") + Math.Abs(delta).ToString("0") + "%",
                Color = delta >= 2 ? Theme.Good : delta <= -2 ? Theme.Hot : Theme.Ink,
            });
        }
        _card = items;
        _cardUntil = Environment.TickCount64 + ms;
    }

    // ============================================================== layout

    const float Ear = 7;
    const int AmbientMs = 50;

    /// <summary>The theme moves on its own (aurora, shimmer, scanlines) and motion is allowed.</summary>
    bool Ambient => _shown && _config.Motion && _config.Animate && Theme.Current.Animated;

    float Dpi => DeviceDpi / 96f * (float)Math.Clamp(_config.Scale, 0.7, 1.8);
    float S(float v) => v * Dpi;

    void EnsureFonts()
    {
        Theme.Use(_config.Accent);
        if (_fLabel != null && _fontScale == Dpi && _fontTheme == Theme.Current.Name) return;
        foreach (var f in new[] { _fLabel, _fValue, _fBig, _fTitle, _fText, _fChip, _fTiny, _fRing, _fHuge }) f?.Dispose();
        _fontScale = Dpi;
        var t = Theme.Current;
        _fontTheme = t.Name;
        // Numbers and labels use the theme's instrument face (Bahnschrift, a
        // DIN-style font that ships with Windows, its condensed cuts, or Cascadia
        // Mono); sentences use the theme's text face. NumScale evens out widths.
        string regular = Theme.Font(t.NumRegular, "Bahnschrift"), bold = Theme.Font(t.NumBold, "Bahnschrift SemiBold");
        string title = Theme.Font(t.TitleFont, "Segoe UI Semibold"), text = Theme.Font(t.TextFont, "Segoe UI");
        float k = t.NumScale, tk = text.StartsWith("Cascadia") ? 0.9f : 1f;
        _fLabel = new Font(regular, S(10.5f * k), FontStyle.Regular, GraphicsUnit.Pixel);
        _fValue = new Font(bold, S(15.5f * k), FontStyle.Regular, GraphicsUnit.Pixel);
        _fBig = new Font(bold, S(19f * k), FontStyle.Regular, GraphicsUnit.Pixel);
        _fHuge = new Font(bold, S(26f * k), FontStyle.Regular, GraphicsUnit.Pixel);
        _fTitle = new Font(title, S(13.5f * tk), FontStyle.Regular, GraphicsUnit.Pixel);
        _fText = new Font(text, S(12.5f * tk), FontStyle.Regular, GraphicsUnit.Pixel);
        _fChip = new Font(bold, S(10f * k), FontStyle.Regular, GraphicsUnit.Pixel);
        _fTiny = new Font(text, S(10f * tk), FontStyle.Regular, GraphicsUnit.Pixel);
        _fRing = new Font(bold, S(9.5f * k), FontStyle.Regular, GraphicsUnit.Pixel);
    }

    static Color ScoreColor(float score) => score >= 80 ? Theme.Good : score >= 55 ? Theme.Warn : Theme.Hot;

    void UpdateLayout(long now)
    {
        EnsureFonts();
        if (_alert != null && now >= _alertUntil) _alert = null;
        if (_card != null && now >= _cardUntil) _card = null;

        View v = _panel ? View.Panel
            : _alert != null ? View.Alert
            : _card != null ? View.Card
            : _config.Animate && now - _bootStart < BootMs ? View.Boot
            : _inGame && _config.Intro && _session!.IntroActive(now) ? View.Intro
            : _inGame ? View.Compact
            : View.Idle;
        // The wide bar replaces the small notch; alerts and result cards show in its centre.
        if (_config.Wide && v is View.Idle or View.Compact or View.Alert or View.Card) v = View.Wide;

        BuildRow(v);
        float tw, th;
        if (v == View.Panel)
        {
            tw = S(600);
            th = PanelLayout(null, tw);
        }
        else if (v == View.Wide)
        {
            float screenW = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds.Width;
            th = S(38);
            FitWide(screenW);
            // Only as wide as the content: the three groups side by side.
            float groups = RowWidth(_row) + RowWidth(_right) + (_mid.Count > 0 ? RowWidth(_mid) + S(36) : 0) + S(36);
            tw = Math.Min(screenW, (float)Math.Ceiling(groups + 2 * (S(Ear) + S(22))));
        }
        else
        {
            float content = RowWidth(_row);
            th = S(v == View.Idle ? 28 : 32);
            if (v == View.Card && _cardNote.Length > 0)
            {
                content = Math.Max(content, W(_cardNote, _fText!));
                th = S(56);
            }
            else if (v == View.Intro && _session is { Checks: { } checks } intro)
            {
                int shown = intro.ChecksShown(now);
                for (int i = 0; i < shown; i++) content = Math.Max(content, W(checks[i].Text, _fText!) + S(22));
                if (shown > 0) th = S(36) + shown * S(21) + S(8);
            }
            tw = (float)Math.Ceiling(content + 2 * S(18) + 2 * S(Ear));
        }

        bool first = _w <= 0;
        if (v != _view) { _view = v; if (!first) _fade = 0; }
        _tw = tw;
        _th = th;
        if (first) { _w = v == View.Boot ? S(30) : tw; _h = th; } // the boot sequence opens out from a dot
        if (Math.Abs(_tw - _w) > 0.5f || Math.Abs(_th - _h) > 0.5f || _fade < 1 || v == View.Boot)
        {
            _animTimer.Interval = 15;
            _animTimer.Start();
        }
        else if (Ambient && !_animTimer.Enabled)
        {
            _animTimer.Interval = AmbientMs;
            _animTimer.Start();
        }
    }

    void BuildRow(View v)
    {
        _row.Clear();
        if (v == View.Wide) { BuildWide(); return; }
        Color accent = Theme.Accent(_config.Accent);
        float cpu = _sensors.CpuTemp, gpu = _sensors.GpuTemp;
        var s = _session;

        switch (v)
        {
            case View.Alert when _alert != null:
                _row.Add(new Item { Kind = K.Dot, Color = _alert.Color });
                _row.Add(new Item { Kind = K.Title, A = _alert.Title, Color = Theme.Ink });
                _row.Add(new Item { Kind = K.Text, A = _alert.Detail, Color = Theme.Soft });
                return;

            case View.Card when _card != null:
                _row.AddRange(_card);
                return;

            case View.Boot:
            {
                // Start-up sequence: the line types itself out, then reports ready.
                const string line = "NOTCHHUD  //  SYSTEMS ONLINE";
                int chars = (int)Math.Clamp((Environment.TickCount64 - _bootStart - 400) / 32, 0, line.Length);
                _row.Add(new Item { Kind = K.Dot, Color = accent });
                _row.Add(new Item { Kind = K.Pair, A = line[..chars], B = chars < line.Length ? "_" : "", Color = accent });
                if (chars == line.Length) _row.Add(new Item { Kind = K.Chip, A = "READY", Color = Theme.Good });
                return;
            }

            case View.Intro when s != null:
                if (s.Icon != null) _row.Add(new Item { Kind = K.Icon, Img = s.Icon });
                _row.Add(new Item { Kind = K.Title, A = s.Name, Color = Theme.Ink });
                if (s.Checks is not { } checks)
                    _row.Add(new Item { Kind = K.Chip, A = "SCANNING", Color = accent });
                else if (s.ChecksShown(Environment.TickCount64) < checks.Count || checks.Count == 0)
                    _row.Add(new Item { Kind = K.Chip, A = "PRE-FLIGHT", Color = accent });
                else
                {
                    int bad = checks.Count(c => !c.Ok);
                    _row.Add(bad == 0
                        ? new Item { Kind = K.Chip, A = "ALL SYSTEMS GO", Color = Theme.Good }
                        : new Item { Kind = K.Chip, A = bad == 1 ? "1 THING TO CHECK" : $"{bad} THINGS TO CHECK", Color = Theme.Warn });
                }
                if (s.Usual is { Sessions: >= 2 } usual)
                    _row.Add(new Item { Kind = K.Pair, A = "YOUR USUAL", B = Num(usual.Avg) + " FPS", Color = Theme.Soft });
                else if (s.Previous != null)
                    _row.Add(new Item { Kind = K.Pair, A = "LAST TIME", B = Num(s.Previous.Avg) + " FPS", Color = Theme.Soft });
                return;
        }

        bool game = _inGame && s != null && (v == View.Compact || v == View.Panel || v == View.Intro);
        if (game)
        {
            if (s!.Icon != null) _row.Add(new Item { Kind = K.Icon, Img = s.Icon });
            if (_config.ShowGraph && v != View.Panel) _row.Add(new Item { Kind = K.Spark, Color = accent });

            // FPS turns amber, then red, when it dips well below the pace of the last few seconds.
            Color fpsColor = _stats.Phase is { Length: > 0 } ? Theme.Label
                : _pace > 0 && _stats.Current < _pace * 0.6f ? Theme.Hot
                : _pace > 0 && _stats.Current < _pace * 0.8f ? Theme.Warn
                : Theme.Ink;
            _row.Add(new Item { Kind = K.BigPair, A = "FPS", B = Num(_fpsShown), Color = fpsColor });
            _row.Add(new Item { Kind = K.Pair, A = "AVG", B = Num(_stats.Average), Color = Theme.Ink });
            _row.Add(new Item { Kind = K.Pair, A = "1%", B = Num(_stats.Low1), Color = Theme.Ink });
            _row.Add(new Item { Kind = K.Pair, A = "0.1%", B = Num(_stats.Low01), Color = Theme.Ink });
            if (_advisor.Score >= 0 && v != View.Panel)
                _row.Add(new Item { Kind = K.Ring, A = _advisor.Score.ToString(), V = _advisor.Score, Color = ScoreColor(_advisor.Score) });
            _row.Add(new Item { Kind = K.Sep });
        }

        if (!game)
        {
            _row.Add(new Item { Kind = K.Title, A = DateTime.Now.ToString("HH:mm"), Color = Theme.Ink });
            _row.Add(new Item { Kind = K.Sep });
        }
        _row.Add(new Item { Kind = K.Pair, A = "CPU", B = Temp(cpu), Color = TempColor(cpu, 75, 88) });
        _row.Add(new Item { Kind = K.Pair, A = "GPU", B = Temp(gpu), Color = TempColor(gpu, 75, 85) });
        if (game && _config.NetWatch && _net.Net.Samples > 0)
        {
            float ping = _net.LastMs;
            _row.Add(new Item
            {
                Kind = K.Pair, A = "PING", B = float.IsNaN(ping) ? "LOST" : Math.Round(ping).ToString("0"),
                Color = float.IsNaN(ping) || ping >= 120 ? Theme.Hot : ping >= 60 ? Theme.Warn : Theme.Good,
            });
        }
        bool throttling = _sensors.CpuThrottle.Length > 0 || _sensors.GpuThrottle.Length > 0;
        if (!game && throttling)
            _row.Add(new Item { Kind = K.Chip, A = _sensors.CpuThrottle.Length > 0 ? "CPU THROTTLING" : "GPU THROTTLING", Color = Theme.Hot });
        if (!game && _config.NowPlaying && _sensors.NowPlaying is { Length: > 0 } track)
        {
            _row.Add(new Item { Kind = K.Sep });
            _row.Add(new Item { Kind = K.Dot, Color = accent });
            _row.Add(new Item { Kind = K.Text, A = Clip(track, 44), Color = Theme.Soft });
        }

        if (game)
        {
            if (s!.Benching)
            {
                _row.Add(new Item { Kind = K.Sep });
                _row.Add(new Item { Kind = K.Dot, Color = Theme.Hot });
                _row.Add(new Item
                {
                    Kind = K.Pair, A = "REC",
                    B = Duration((int)((Environment.TickCount64 - s.BenchTick) / 1000)), Color = Theme.Ink,
                });
            }
            else if (throttling)
                _row.Add(new Item { Kind = K.Chip, A = _sensors.CpuThrottle.Length > 0 ? "CPU THROTTLING" : "GPU THROTTLING", Color = Theme.Hot });
            else if (_stats.Phase is { Length: > 0 } phase)
                _row.Add(new Item
                {
                    Kind = K.Chip,
                    A = phase == "cutscene" ? $"CUTSCENE · {Math.Round(_stats.PhaseFps):0}" : phase == "loading" ? "LOADING" : "MENU",
                    Color = phase == "cutscene" ? accent : Theme.Label,
                });
            else if (_advisor.Tag.Length > 0)
            {
                Color c = _advisor.Tag == Advisor.GpuBound ? accent
                    : _advisor.Tag == Advisor.CpuBound ? Theme.Warn
                    : Theme.Label;
                _row.Add(new Item { Kind = K.Chip, A = _advisor.Tag, Color = c });
            }
        }
    }

    /// <summary>
    /// The wide bar: the game on the left (FPS, lows, a long frame-time graph,
    /// smoothness and what is limiting it), the clock or the current alert in
    /// the centre, and every vital sign on the right with load bars.
    /// </summary>
    void BuildWide()
    {
        _mid.Clear();
        _right.Clear();
        Color accent = Theme.Accent(_config.Accent);
        var m = _sensors;
        var s = _session;
        bool game = _inGame && s != null;
        bool throttling = m.CpuThrottle.Length > 0 || m.GpuThrottle.Length > 0;

        // ---- left: the game
        if (game)
        {
            if (s!.Icon != null) _row.Add(new Item { Kind = K.Icon, Img = s.Icon });
            _row.Add(new Item { Kind = K.Title, A = Clip(s.Name, 24), Color = Theme.Ink });
            _row.Add(new Item { Kind = K.Sep });
            Color fpsColor = _stats.Phase is { Length: > 0 } ? Theme.Label
                : _pace > 0 && _stats.Current < _pace * 0.6f ? Theme.Hot
                : _pace > 0 && _stats.Current < _pace * 0.8f ? Theme.Warn
                : Theme.Ink;
            _row.Add(new Item { Kind = K.BigPair, A = "FPS", B = Num(_fpsShown), Color = fpsColor });
            _row.Add(new Item { Kind = K.Pair, A = "AVG", B = Num(_stats.Average), Color = Theme.Ink });
            _row.Add(new Item { Kind = K.Pair, A = "1%", B = Num(_stats.Low1), Color = Theme.Ink });
            _row.Add(new Item { Kind = K.Pair, A = "0.1%", B = Num(_stats.Low01), Color = Theme.Ink });
            float ms = _stats.Current > 0 ? 1000f / _stats.Current : 0;
            if (ms > 0) _row.Add(new Item { Kind = K.Small, B = $"{ms:0.0} ms", Color = Theme.Soft });
            if (_config.ShowGraph) _row.Add(new Item { Kind = K.Spark, Color = accent, V = 190 });
            if (_advisor.Score >= 0)
                _row.Add(new Item { Kind = K.Ring, A = _advisor.Score.ToString(), V = _advisor.Score, Color = ScoreColor(_advisor.Score) });
            if (_stats.Stutters10s > 0)
                _row.Add(new Item { Kind = K.Pair, A = "STUTTER", B = _stats.Stutters10s.ToString(),
                                    Color = _stats.Stutters10s >= 5 ? Theme.Hot : Theme.Warn });

            if (s.Benching)
            {
                _row.Add(new Item { Kind = K.Dot, Color = Theme.Hot });
                _row.Add(new Item { Kind = K.Pair, A = "REC", B = Duration((int)((Environment.TickCount64 - s.BenchTick) / 1000)), Color = Theme.Ink });
            }
            else if (_stats.Phase is { Length: > 0 } phase)
                _row.Add(new Item
                {
                    Kind = K.Chip,
                    A = phase == "cutscene" ? $"CUTSCENE · {Math.Round(_stats.PhaseFps):0}" : phase == "loading" ? "LOADING" : "MENU",
                    Color = phase == "cutscene" ? accent : Theme.Label,
                });
            else if (_advisor.Tag.Length > 0)
                _row.Add(new Item
                {
                    Kind = K.Chip, A = _advisor.Tag,
                    Color = _advisor.Tag == Advisor.GpuBound ? accent : _advisor.Tag == Advisor.CpuBound ? Theme.Warn : Theme.Label,
                });
        }
        else
        {
            _row.Add(new Item { Kind = K.Dot, Color = accent });
            if (s != null) _row.Add(new Item { Kind = K.Text, A = Clip(s.Name, 30) + " in background", Color = Theme.Soft });
            else if (_config.NowPlaying && m.NowPlaying is { Length: > 0 } track)
                _row.Add(new Item { Kind = K.Text, A = Clip(track, 50), Color = Theme.Soft });
            else _row.Add(new Item { Kind = K.Text, A = "No game running", Color = Theme.Label });
        }

        // ---- centre: alert, result card, or the clock
        if (_alert != null)
        {
            _mid.Add(new Item { Kind = K.Dot, Color = _alert.Color });
            _mid.Add(new Item { Kind = K.Title, A = _alert.Title, Color = Theme.Ink });
            _mid.Add(new Item { Kind = K.Text, A = _alert.Detail, Color = Theme.Soft });
        }
        else if (_card != null)
        {
            _mid.AddRange(_card);
            if (_cardNote.Length > 0) _mid.Add(new Item { Kind = K.Text, A = _cardNote, Color = Theme.Soft });
        }
        else
        {
            _mid.Add(new Item { Kind = K.Title, A = DateTime.Now.ToString("HH:mm"), Color = Theme.Ink });
            if (game) _mid.Add(new Item { Kind = K.Small, B = Duration((int)((Environment.TickCount64 - s!.StartTick) / 1000)), Color = Theme.Soft });
            else _mid.Add(new Item { Kind = K.Small, B = DateTime.Now.ToString("ddd d MMM"), Color = Theme.Soft });
        }

        // ---- right: the system
        static string Ghz(float mhz) => float.IsNaN(mhz) || mhz <= 0 ? "" : $"{mhz / 1000f:0.0} GHz";
        if (throttling)
            _right.Add(new Item { Kind = K.Chip, A = m.CpuThrottle.Length > 0 ? "CPU THROTTLING" : "GPU THROTTLING", Color = Theme.Hot });

        _right.Add(new Item { Kind = K.Meter, A = "CPU", B = Pct(m.CpuLoad), V = m.CpuLoad, Color = Level(m.CpuLoad, accent) });
        _right.Add(new Item { Kind = K.Val, B = Temp(m.CpuTemp), Color = TempColor(m.CpuTemp, 75, 88) });
        string cpuInfo = Ghz(m.CpuClockMHz) + (float.IsNaN(m.CpuPowerW) ? "" : $" · {m.CpuPowerW:0} W");
        if (cpuInfo.Trim().Length > 0) _right.Add(new Item { Kind = K.Small, B = cpuInfo.Trim(' ', '·'), Color = Theme.Soft });
        _right.Add(new Item { Kind = K.Sep });

        _right.Add(new Item { Kind = K.Meter, A = "GPU", B = Pct(m.GpuLoad), V = m.GpuLoad, Color = Level(m.GpuLoad, accent) });
        _right.Add(new Item { Kind = K.Val, B = Temp(m.GpuTemp), Color = TempColor(m.GpuTemp, 75, 85) });
        string gpuInfo = Ghz(m.GpuClockMHz) + (float.IsNaN(m.GpuPowerW) ? "" : $" · {m.GpuPowerW:0} W");
        if (gpuInfo.Trim().Length > 0) _right.Add(new Item { Kind = K.Small, B = gpuInfo.Trim(' ', '·'), Color = Theme.Soft });
        _right.Add(new Item { Kind = K.Sep });

        float vram = m.VramPct, ram = m.RamPct;
        _right.Add(new Item { Kind = K.Meter, A = "VRAM", B = float.IsNaN(vram) ? "–" : $"{m.VramUsedMB / 1024:0.0}G", V = vram, Color = Level(vram, accent) });
        _right.Add(new Item { Kind = K.Meter, A = "RAM", B = $"{m.RamUsedGB:0.0}G", V = ram, Color = Level(ram, accent) });

        if (_config.NetWatch && _net.Net.Samples > 0)
        {
            float ping = _net.LastMs;
            _right.Add(new Item { Kind = K.Sep });
            _right.Add(new Item
            {
                Kind = K.Pair, A = "PING", B = float.IsNaN(ping) ? "LOST" : Math.Round(ping).ToString("0"),
                Color = float.IsNaN(ping) || ping >= 120 ? Theme.Hot : ping >= 60 ? Theme.Warn : Theme.Good,
            });
        }
    }

    /// <summary>Drops the least useful items until the three groups fit side by side.</summary>
    void FitWide(float width)
    {
        float pad = 2 * (S(Ear) + S(22)), spacing = 2 * S(36);
        bool Fits() => RowWidth(_row) + RowWidth(_mid) + RowWidth(_right) + pad + spacing <= width;
        bool alerting = _alert != null || _card != null;

        // Cheapest losses first: the clock line, clock speeds, the game name, a shorter graph, then the note.
        if (!Fits() && !alerting) _mid.RemoveAll(i => i.Kind == K.Small);
        if (!Fits()) _right.RemoveAll(i => i.Kind == K.Small);
        if (!Fits()) _row.RemoveAll(i => i.Kind == K.Small);
        if (!Fits()) { int t = _row.FindIndex(i => i.Kind == K.Title); if (t >= 0) { _row.RemoveAt(t); if (t < _row.Count && _row[t].Kind == K.Sep) _row.RemoveAt(t); } }
        if (!Fits()) for (int i = 0; i < _row.Count; i++) if (_row[i].Kind == K.Spark) _row[i] = _row[i] with { V = 96 };
        if (!Fits() && !alerting) _mid.Clear();
        if (!Fits() && _mid.Count > 0 && _mid[^1].Kind == K.Text)
        {
            // Shorten the alert detail rather than overlap.
            var last = _mid[^1];
            while (!Fits() && last.A.Length > 12) { last.A = Clip(last.A, last.A.Length - 6); _mid[^1] = last; }
        }
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    static string Num(float v) => v > 0 && !float.IsInfinity(v) && !float.IsNaN(v) ? Math.Round(v).ToString("0") : "–";
    static string Temp(float v) => float.IsNaN(v) ? "–" : Math.Round(v).ToString("0") + "°";
    static Color TempColor(float v, float warn, float hot) =>
        float.IsNaN(v) ? Theme.Label : v >= hot ? Theme.Hot : v >= warn ? Theme.Warn : Theme.Good;

    static string Duration(int sec) =>
        sec >= 3600 ? $"{sec / 3600}:{sec / 60 % 60:00}:{sec % 60:00}" : $"{sec / 60}:{sec % 60:00}";

    // ----------------------------------------------------------- animation

    void Animate()
    {
        const float dt = 0.016f, stiffness = 300f, damping = 28f;
        _vw += ((_tw - _w) * stiffness - _vw * damping) * dt;
        _vh += ((_th - _h) * stiffness - _vh * damping) * dt;
        _w += _vw * dt;
        _h += _vh * dt;
        _fade = Math.Min(1f, _fade + 0.09f);

        long now = Environment.TickCount64;
        bool counting = _fpsShown != _fpsTo;
        if (counting)
        {
            float t = (now - _tweenStart) / (float)TweenMs;
            _fpsShown = t >= 1 ? _fpsTo : _fpsFrom + (_fpsTo - _fpsFrom) * (1 - (1 - t) * (1 - t));
        }
        // Views whose content changes between data ticks are laid out again every frame.
        bool live = counting || _view == View.Boot;
        if (live && _shown) UpdateLayout(now);

        if (Math.Abs(_tw - _w) < 0.4f && Math.Abs(_th - _h) < 0.4f && Math.Abs(_vw) < 6 && Math.Abs(_vh) < 6 && _fade >= 1)
        {
            _w = _tw; _h = _th; _vw = _vh = 0;
            if (now >= _pulseUntil && !live)
            {
                // Settled. A living theme keeps redrawing, but only a few times a second
                // so it never competes with the game; otherwise stop until something changes.
                if (Ambient) _animTimer.Interval = AmbientMs;
                else _animTimer.Stop();
            }
        }
        else _animTimer.Interval = 15;
        Render();
    }

    // ============================================================= drawing

    float W(string s, Font f) => s.Length == 0 ? 0 : _measure.MeasureString(s, f, PointF.Empty, _fmt).Width;

    static float Ascent(Font f) =>
        f.Size * f.FontFamily.GetCellAscent(f.Style) / f.FontFamily.GetEmHeight(f.Style);

    Color Fade(Color c) => _fade >= 1 ? c : Color.FromArgb((int)(c.A * _fade), c);

    float Draw(Graphics g, string s, Font f, Color c, float x, float baseline)
    {
        if (s.Length == 0) return 0;
        using var b = new SolidBrush(Fade(c));
        g.DrawString(s, f, b, x, baseline - Ascent(f), _fmt);
        return W(s, f);
    }

    float ItemWidth(in Item it) => it.Kind switch
    {
        K.Pair => W(it.A, _fLabel!) + S(5) + W(it.B, _fValue!),
        K.BigPair => W(it.B, _fBig!) + S(5) + W(it.A, _fLabel!),
        K.Sep => S(1),
        K.Icon => S(18),
        K.Spark => S(it.V > 0 ? it.V : 64),
        K.Meter => Math.Max(S(58), W(it.A, _fLabel!) + S(5) + W(it.B, _fValue!)),
        K.Val => W(it.B, _fValue!),
        K.Small => W(it.B, _fLabel!),
        K.Chip => W(it.A, _fChip!) + S(14),
        K.Dot => S(8),
        K.Title => W(it.A, _fTitle!),
        K.Text => W(it.A, _fText!),
        K.Ring => S(24),
        _ => 0,
    };

    /// <summary>A 270° gauge arc with a value in the middle.</summary>
    void DrawRing(Graphics g, float cx, float cy, float d, float thickness, float pct, Color color, string center, Font font)
    {
        var rect = new RectangleF(cx - d / 2, cy - d / 2, d, d);
        bool has = !float.IsNaN(pct) && pct > 0;
        float sweep = has ? 270f * Math.Min(100f, pct) / 100f : 0;
        switch (Theme.Current.Ring)
        {
            case RingStyle.Ticks:
            {
                // Radial ticks around the 270° arc, lit up to the value.
                int n = Math.Clamp((int)(d / S(2.1f)), 12, 44);
                float outer = d / 2 + thickness * 0.5f, inner = d / 2 - thickness * 0.9f;
                using var off = new Pen(Fade(Theme.Line(40)), Math.Max(1f, thickness * 0.42f));
                using var on = new Pen(Fade(color), Math.Max(1f, thickness * 0.5f));
                using var glow = new Pen(Fade(Color.FromArgb(60, color)), Math.Max(1f, thickness * 1.3f));
                for (int i = 0; i < n; i++)
                {
                    float a = 135f + 270f * i / (n - 1);
                    bool lit = has && 270f * i / (n - 1) <= sweep;
                    double rad = a * Math.PI / 180;
                    float c = (float)Math.Cos(rad), s = (float)Math.Sin(rad);
                    var p1 = new PointF(cx + c * inner, cy + s * inner);
                    var p2 = new PointF(cx + c * outer, cy + s * outer);
                    if (lit) g.DrawLine(glow, p1, p2);
                    g.DrawLine(lit ? on : off, p1, p2);
                }
                break;
            }
            case RingStyle.Thin:
            {
                // Hairline track, slim arc and a bright bead at the tip.
                float t = Math.Max(1f, thickness * 0.55f);
                using (var track = new Pen(Fade(Theme.Line(30)), Math.Max(1f, t * 0.6f)))
                    g.DrawArc(track, rect, 135, 270);
                if (has)
                {
                    using (var halo = new Pen(Fade(Color.FromArgb(40, color)), t * 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(halo, rect, 135, sweep);
                    using (var pen = new Pen(Fade(color), t) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(pen, rect, 135, sweep);
                    double rad = (135 + sweep) * Math.PI / 180;
                    float bx = cx + (float)Math.Cos(rad) * d / 2, by = cy + (float)Math.Sin(rad) * d / 2, br = t * 1.5f;
                    using var bead = new SolidBrush(Fade(Color.FromArgb(70, color)));
                    g.FillEllipse(bead, bx - br * 2, by - br * 2, br * 4, br * 4);
                    using var core = new SolidBrush(Fade(Theme.Ink));
                    g.FillEllipse(core, bx - br, by - br, br * 2, br * 2);
                }
                break;
            }
            default:
                using (var track = new Pen(Fade(Theme.Line(34)), thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(track, rect, 135, 270);
                if (has)
                {
                    using (var halo = new Pen(Fade(Color.FromArgb(46, color)), thickness * 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(halo, rect, 135, sweep);
                    // The arc brightens toward its tip.
                    using var brush = new LinearGradientBrush(rect, Color.FromArgb(150, color), color, 45f);
                    using var pen = new Pen(brush, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    if (_fade < 1) pen.Color = Fade(color);
                    g.DrawArc(pen, rect, 135, sweep);
                }
                break;
        }
        Draw(g, center, font, Theme.Ink, cx - W(center, font) / 2, cy + font.Size * 0.36f);
    }

    float Gap(List<Item> r, int i)
    {
        if (i == 0) return 0;
        K p = r[i - 1].Kind, c = r[i].Kind;
        if (p is K.Icon or K.Dot) return S(8);
        if (c is K.Val or K.Small && p is not K.Sep) return S(7); // a reading's details sit close to it
        if (p == K.Sep || c == K.Sep) return S(12);
        return S(14);
    }

    float RowWidth(List<Item> r)
    {
        float w = 0;
        for (int i = 0; i < r.Count; i++) w += Gap(r, i) + ItemWidth(r[i]);
        return w;
    }

    void DrawRow(Graphics g, List<Item> r, float x, float top, float h)
    {
        float cy = top + h / 2f, baseline = cy + S(5);
        for (int i = 0; i < r.Count; i++)
        {
            Item it = r[i];
            x += Gap(r, i);
            float w = ItemWidth(it);
            switch (it.Kind)
            {
                case K.Pair:
                    Draw(g, it.B, _fValue!, it.Color, x + Draw(g, it.A, _fLabel!, Theme.Label, x, baseline) + S(5), baseline);
                    break;
                case K.BigPair:
                    Draw(g, it.A, _fLabel!, Theme.Label, x + Draw(g, it.B, _fBig!, it.Color, x, baseline + S(0.5f)) + S(5), baseline);
                    break;
                case K.Title:
                    Draw(g, it.A, _fTitle!, it.Color, x, baseline - S(0.5f));
                    break;
                case K.Text:
                    Draw(g, it.A, _fText!, it.Color, x, baseline - S(0.5f));
                    break;
                case K.Sep:
                    switch (Theme.Current.Sep)
                    {
                        case SepStyle.Dot:
                            using (var b = new SolidBrush(Fade(Theme.Line(90))))
                                g.FillEllipse(b, x + S(0.5f) - S(1.5f), cy - S(1.5f), S(3), S(3));
                            break;
                        case SepStyle.Slash:
                            using (var pen = new Pen(Fade(Color.FromArgb(110, Theme.Current.Accent)), Math.Max(1f, S(1.2f))))
                                g.DrawLine(pen, x + S(3), cy - S(7), x - S(2.5f), cy + S(7));
                            break;
                        default:
                            using (var pen = new Pen(Fade(Theme.Line(70)), Math.Max(1f, S(1))))
                                g.DrawLine(pen, x, cy - S(7), x, cy + S(7));
                            break;
                    }
                    break;
                case K.Dot:
                    DrawDot(g, x + S(4), cy, S(4), it.Color);
                    break;
                case K.Icon:
                    if (it.Img != null && _fade > 0.4f)
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(it.Img, x, cy - S(9), S(18), S(18));
                    }
                    break;
                case K.Chip:
                    DrawChip(g, new RectangleF(x, cy - S(9), w, S(18)), it.A, it.Color);
                    break;
                case K.Spark:
                    DrawSpark(g, new RectangleF(x, cy - S(9), w, S(18)), it.Color, SparkTop(), it.V > 0 ? 600 : 240, fill: true);
                    break;
                case K.Meter:
                {
                    float bl = baseline - S(2.5f);
                    Draw(g, it.B, _fValue!, Theme.Ink, x + Draw(g, it.A, _fLabel!, Theme.Label, x, bl) + S(5), bl);
                    DrawMeter(g, new RectangleF(x, cy + S(7), w, S(2.5f)), it.V, it.Color);
                    break;
                }
                case K.Val:
                    Draw(g, it.B, _fValue!, it.Color, x, baseline - S(2.5f));
                    break;
                case K.Small:
                    Draw(g, it.B, _fLabel!, it.Color, x, baseline - S(2.5f));
                    break;
                case K.Ring:
                    DrawRing(g, x + w / 2, cy + S(1), S(22), S(2.4f), it.V, it.Color, it.A, _fRing!);
                    break;
            }
            x += w;
        }
    }

    /// <summary>A status dot in the theme's shape, with a soft halo.</summary>
    void DrawDot(Graphics g, float cx, float cy, float r, Color color)
    {
        using var halo = new SolidBrush(Fade(Color.FromArgb(Theme.Current.Light ? 40 : 55, color)));
        using var b = new SolidBrush(Fade(color));
        switch (Theme.Current.Dot)
        {
            case DotStyle.Square:
                g.FillRectangle(halo, cx - r * 1.5f, cy - r * 1.5f, r * 3, r * 3);
                g.FillRectangle(b, cx - r * 0.85f, cy - r * 0.85f, r * 1.7f, r * 1.7f);
                break;
            case DotStyle.Diamond:
            {
                PointF[] Dia(float k) => new[] { new PointF(cx, cy - r * k), new PointF(cx + r * k, cy), new PointF(cx, cy + r * k), new PointF(cx - r * k, cy) };
                g.FillPolygon(halo, Dia(1.7f));
                g.FillPolygon(b, Dia(1.05f));
                break;
            }
            default:
                g.FillEllipse(halo, cx - r * 1.6f, cy - r * 1.6f, r * 3.2f, r * 3.2f);
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
                break;
        }
    }

    /// <summary>A status chip: tinted pill, outline, [bracketed] or a solid angled tag.</summary>
    void DrawChip(Graphics g, RectangleF r, string text, Color color)
    {
        var t = Theme.Current;
        Color ink = color;
        switch (t.Chip)
        {
            case ChipStyle.Outline:
                using (var path = Rounded(r, R(9)))
                {
                    using (var b = new SolidBrush(Fade(Color.FromArgb(22, color)))) g.FillPath(b, path);
                    using (var pen = new Pen(Fade(Color.FromArgb(170, color)), Math.Max(1f, S(1)))) g.DrawPath(pen, path);
                }
                break;
            case ChipStyle.Bracket:
            {
                float arm = S(3.5f);
                using var pen = new Pen(Fade(color), Math.Max(1f, S(1.3f)));
                g.DrawLines(pen, new[] { new PointF(r.Left + arm, r.Top + S(1)), new PointF(r.Left, r.Top + S(1)), new PointF(r.Left, r.Bottom - S(1)), new PointF(r.Left + arm, r.Bottom - S(1)) });
                g.DrawLines(pen, new[] { new PointF(r.Right - arm, r.Top + S(1)), new PointF(r.Right, r.Top + S(1)), new PointF(r.Right, r.Bottom - S(1)), new PointF(r.Right - arm, r.Bottom - S(1)) });
                using var b = new SolidBrush(Fade(Color.FromArgb(18, color)));
                g.FillRectangle(b, r);
                break;
            }
            case ChipStyle.Tag:
            {
                // Solid block with the top-left corner cut off, text knocked out.
                float c = S(5);
                var pts = new[] { new PointF(r.Left + c, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom - S(2)),
                                  new PointF(r.Right - S(2), r.Bottom), new PointF(r.Left, r.Bottom), new PointF(r.Left, r.Top + c) };
                using (var glow = new Pen(Fade(Color.FromArgb(60, color)), S(3)) { LineJoin = LineJoin.Miter })
                    g.DrawPolygon(glow, pts);
                using (var b = new SolidBrush(Fade(color))) g.FillPolygon(b, pts);
                ink = t.OnAccent;
                break;
            }
            default:
                using (var path = Rounded(r, R(9)))
                using (var b = new SolidBrush(Fade(Color.FromArgb(t.Light ? 34 : 46, color))))
                    g.FillPath(b, path);
                break;
        }
        Draw(g, text, _fChip!, ink, r.X + S(7), r.Y + S(9) + S(3.4f));
    }

    /// <summary>A thin progress track under a reading: line, LED segments or a glowing capsule.</summary>
    void DrawMeter(Graphics g, RectangleF track, float pct, Color color)
    {
        bool has = !float.IsNaN(pct) && pct > 0;
        float f = has ? Math.Min(100f, pct) / 100f : 0;
        switch (Theme.Current.Meter)
        {
            case MeterStyle.Segments:
            {
                var tr = track with { Y = track.Y - S(0.5f), Height = track.Height + S(1) };
                float gap = Math.Max(1f, S(1.5f));
                int n = Math.Max(6, (int)((tr.Width + gap) / S(6)));
                float sw = (tr.Width - gap * (n - 1)) / n;
                int lit = has ? Math.Max(1, (int)Math.Round(f * n)) : 0;
                using var off = new SolidBrush(Fade(Theme.Line(30)));
                using var on = new SolidBrush(Fade(color));
                using var hot = new SolidBrush(Fade(Color.FromArgb(70, color)));
                for (int i = 0; i < n; i++)
                {
                    var seg = new RectangleF(tr.X + i * (sw + gap), tr.Y, sw, tr.Height);
                    if (i < lit) { g.FillRectangle(hot, RectangleF.Inflate(seg, S(0.5f), S(1.2f))); g.FillRectangle(on, seg); }
                    else g.FillRectangle(off, seg);
                }
                break;
            }
            case MeterStyle.Capsule:
            {
                var tr = track with { Y = track.Y - S(0.5f), Height = track.Height + S(1) };
                using (var path = Rounded(tr, tr.Height / 2))
                using (var b = new SolidBrush(Fade(Theme.Line(28))))
                    g.FillPath(b, path);
                if (!has) break;
                var fill = tr with { Width = Math.Max(tr.Height, tr.Width * f) };
                using (var path = Rounded(RectangleF.Inflate(fill, S(1), S(1)), (tr.Height + S(2)) / 2))
                using (var b = new SolidBrush(Fade(Color.FromArgb(50, color))))
                    g.FillPath(b, path);
                using (var path = Rounded(fill, tr.Height / 2))
                using (var b = new LinearGradientBrush(RectangleF.Inflate(fill, 1, 1), Color.FromArgb(Fade(color).A * 3 / 5, color), Fade(color), LinearGradientMode.Horizontal))
                    g.FillPath(b, path);
                break;
            }
            default:
                using (var tb = new SolidBrush(Fade(Theme.Line(34))))
                    g.FillRectangle(tb, track);
                if (has)
                    using (var fb = new SolidBrush(Fade(color)))
                        g.FillRectangle(fb, track with { Width = Math.Max(S(2), track.Width * f) });
                break;
        }
    }

    /// <summary>Corner radius scaled by the theme's roundness.</summary>
    float R(float v) => S(v) * Theme.Current.Round;

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Frame time that maps to the top of the graph.</summary>
    float SparkTop()
    {
        float median = _recentMedian > 0 ? _recentMedian : 16f;
        float max = 0;
        for (int i = 0; i < _recentN; i++) if (_recent[i] > max) max = _recent[i];
        return Math.Max(median * 2.2f, Math.Min(max * 1.1f, median * 6f));
    }

    /// <summary>Frame-time graph of the latest frames: a spike is a stutter.</summary>
    void DrawSpark(Graphics g, RectangleF r, Color color, float top, int frames, bool fill)
    {
        int n = Math.Min(_recentN, frames), start = _recentN - n;
        if (n < 3 || top <= 0) return;
        int cols = Math.Max(2, Math.Min(n, (int)(r.Width / Math.Max(1f, S(1.5f)))));
        var pts = new PointF[cols + 2];
        for (int c = 0; c < cols; c++)
        {
            int a = (int)((long)c * n / cols), b = Math.Max(a + 1, (int)((long)(c + 1) * n / cols));
            float v = 0;
            for (int i = a; i < b && i < n; i++) v = Math.Max(v, _recent[start + i]);
            float t = Math.Min(1f, v / top);
            pts[c] = new PointF(r.Left + r.Width * c / (cols - 1), r.Bottom - 1 - t * (r.Height - 2));
        }

        var gradRect = new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2);
        switch (Theme.Current.Spark)
        {
            case SparkStyle.Bars:
            {
                // One bar per column; tall ones (spikes) get the warning colour.
                float step = r.Width / cols, bw = Math.Max(1f, step - Math.Max(1f, S(0.8f)));
                using var brush = new LinearGradientBrush(gradRect, Fade(color), Fade(Color.FromArgb(60, color)), LinearGradientMode.Vertical);
                using var spike = new SolidBrush(Fade(Theme.Hot));
                float spikeY = r.Bottom - (r.Height - 2) * Math.Min(1f, (_recentMedian > 0 ? _recentMedian * 2f : top) / top);
                for (int c = 0; c < cols; c++)
                {
                    float y = Math.Min(pts[c].Y, r.Bottom - S(1));
                    g.FillRectangle(y < spikeY ? spike : brush, r.Left + c * step, y, bw, r.Bottom - y);
                }
                return;
            }
            case SparkStyle.Step:
            {
                var steps = new PointF[cols * 2];
                for (int c = 0; c < cols; c++)
                {
                    float x1 = r.Left + r.Width * c / cols, x2 = r.Left + r.Width * (c + 1) / cols;
                    steps[2 * c] = new PointF(x1, pts[c].Y);
                    steps[2 * c + 1] = new PointF(x2, pts[c].Y);
                }
                if (fill)
                {
                    var poly = new PointF[steps.Length + 2];
                    steps.CopyTo(poly, 0);
                    poly[^2] = new PointF(r.Right, r.Bottom);
                    poly[^1] = new PointF(r.Left, r.Bottom);
                    using var brush = new LinearGradientBrush(gradRect, Fade(Color.FromArgb(50, color)), Fade(Color.FromArgb(4, color)), LinearGradientMode.Vertical);
                    g.FillPolygon(brush, poly);
                }
                var mode = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None; // crisp, pixel-terminal steps
                using (var stepPen = new Pen(Fade(color), Math.Max(1f, S(1.2f))))
                    g.DrawLines(stepPen, steps);
                g.SmoothingMode = mode;
                return;
            }
        }

        if (fill)
        {
            pts[cols] = new PointF(r.Right, r.Bottom);
            pts[cols + 1] = new PointF(r.Left, r.Bottom);
            using var brush = new LinearGradientBrush(gradRect,
                Fade(Color.FromArgb(90, color)), Fade(Color.FromArgb(8, color)), LinearGradientMode.Vertical);
            g.FillPolygon(brush, pts);
        }
        var line = pts.AsSpan(0, cols).ToArray();
        using (var halo = new Pen(Fade(Color.FromArgb(45, color)), Math.Max(2f, S(3.2f))) { LineJoin = LineJoin.Round })
            g.DrawLines(halo, line);
        using var pen = new Pen(Fade(color), Math.Max(1f, S(1.3f))) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, line);
    }

    void Bar(Graphics g, float x, float y, float w, string label, string value, float pct, Color color)
    {
        Draw(g, label, _fLabel!, Theme.Label, x, y + S(11));
        Draw(g, value, _fText!, Theme.Ink, x + w - W(value, _fText!), y + S(11));
        var track = new RectangleF(x, y + S(18), w, S(4));
        if (Theme.Current.Meter != MeterStyle.Line) { DrawMeter(g, track with { Y = track.Y + S(0.5f), Height = S(3.5f) }, pct, color); return; }
        using (var path = Rounded(track, R(2)))
        using (var b = new SolidBrush(Fade(Theme.Line(30))))
            g.FillPath(b, path);
        if (float.IsNaN(pct) || pct <= 0) return;
        var fillRect = track with { Width = Math.Max(S(4), w * Math.Min(100f, pct) / 100f) };
        using (var path = Rounded(fillRect, R(2)))
        using (var b = new SolidBrush(Fade(color)))
            g.FillPath(b, path);
    }

    Color Level(float pct, Color normal) => pct >= 93 ? Theme.Hot : pct >= 80 ? Theme.Warn : normal;

    static string Pct(float v) => float.IsNaN(v) ? "–" : $"{v:0}%";

    /// <summary>
    /// Radar chart: one axis per vital sign (0 at the centre, 1 at the rim),
    /// with a scanner wedge that steps around it.
    /// </summary>
    void DrawRadar(Graphics g, float cx, float cy, float radius, Color color, (string label, float value)[] axes)
    {
        int n = axes.Length;
        PointF At(int i, float r)
        {
            double a = (-90 + 360.0 * i / n) * Math.PI / 180;
            return new PointF(cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r);
        }

        // Scanner wedge.
        float sweepAngle = (float)(Environment.TickCount64 / 1000.0 * 90 % 360);
        using (var wedge = new SolidBrush(Fade(Color.FromArgb(26, color))))
            g.FillPie(wedge, cx - radius, cy - radius, radius * 2, radius * 2, sweepAngle, 50);

        // Rings and spokes.
        using (var grid = new Pen(Fade(Theme.Line(34)), 1f))
        {
            for (int ring = 1; ring <= 3; ring++)
            {
                var pts = new PointF[n];
                for (int i = 0; i < n; i++) pts[i] = At(i, radius * ring / 3f);
                g.DrawPolygon(grid, pts);
            }
            for (int i = 0; i < n; i++) g.DrawLine(grid, cx, cy, At(i, radius).X, At(i, radius).Y);
        }

        // The shape of the system right now.
        var shape = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            float v = float.IsNaN(axes[i].value) ? 0 : Math.Clamp(axes[i].value, 0.04f, 1f);
            shape[i] = At(i, radius * v);
        }
        using (var fill = new SolidBrush(Fade(Color.FromArgb(70, color))))
            g.FillPolygon(fill, shape);
        using (var halo = new Pen(Fade(Color.FromArgb(50, color)), S(4)) { LineJoin = LineJoin.Round })
            g.DrawPolygon(halo, shape);
        using (var line = new Pen(Fade(color), Math.Max(1f, S(1.4f))) { LineJoin = LineJoin.Round })
            g.DrawPolygon(line, shape);
        using (var dot = new SolidBrush(Fade(Theme.Ink)))
            foreach (var p in shape) g.FillEllipse(dot, p.X - S(1.6f), p.Y - S(1.6f), S(3.2f), S(3.2f));

        for (int i = 0; i < n; i++)
        {
            PointF p = At(i, radius + S(11));
            string label = axes[i].label;
            float w = W(label, _fTiny!);
            float lx = Math.Abs(p.X - cx) < S(4) ? p.X - w / 2 : p.X < cx ? p.X - w + S(4) : p.X - S(4);
            Draw(g, label, _fTiny!, Theme.Label, lx, p.Y + S(3.5f));
        }
    }

    /// <summary>A large ring gauge with a caption and a detail line under it.</summary>
    void Gauge(Graphics g, float cx, float cy, float d, float pct, string center, string label, string detail, Color color)
    {
        DrawRing(g, cx, cy, d, S(4.5f), pct, color, center, _fValue!);
        Draw(g, label, _fLabel!, Theme.Label, cx - W(label, _fLabel!) / 2, cy + d / 2 + S(11));
        Draw(g, detail, _fTiny!, Theme.Soft, cx - W(detail, _fTiny!) / 2, cy + d / 2 + S(25));
    }

    /// <summary>
    /// Lays out the details panel below the top row. With g == null it only
    /// measures, so the same code gives the panel height and draws it.
    /// </summary>
    float PanelLayout(Graphics? g, float w)
    {
        Color accent = Theme.Accent(_config.Accent);
        float x0 = S(Ear) + S(22), cw = w - 2 * x0, y = S(32);
        var m = _sensors;

        if (g != null)
            using (var pen = new Pen(Fade(Theme.Line(30)), 1f))
                g.DrawLine(pen, x0, y, x0 + cw, y);

        if (_inGame)
        {
            y += S(12);
            if (g != null)
            {
                Draw(g, "FRAME TIME", _fLabel!, Theme.Label, x0, y + S(10));
                string right = $"{(_stats.Current > 0 ? 1000f / _stats.Current : 0):0.0} ms now  ·  median {_recentMedian:0.0} ms  ·  {_stats.Stutters10s} stutters / 10 s";
                Draw(g, right, _fTiny!, Theme.Soft, x0 + cw - W(right, _fTiny!), y + S(10));
            }
            y += S(18);
            if (g != null)
            {
                var rect = new RectangleF(x0, y, cw, S(66));
                using (var path = Rounded(rect, R(8)))
                using (var b = new SolidBrush(Fade(Theme.Line(16))))
                    g.FillPath(b, path);
                float top = SparkTop();
                var inner = RectangleF.Inflate(rect, -S(6), -S(6));
                using (var grid = new Pen(Fade(Theme.Line(14)), 1f))
                    for (int i = 1; i <= 7; i++)
                    {
                        float gx = inner.Left + inner.Width * i / 8f;
                        g.DrawLine(grid, gx, inner.Top, gx, inner.Bottom);
                    }
                if (_recentMedian > 0 && top > 0)
                {
                    float my = inner.Bottom - 1 - Math.Min(1f, _recentMedian / top) * (inner.Height - 2);
                    using var dash = new Pen(Fade(Theme.Line(50)), 1f) { DashStyle = DashStyle.Dash };
                    g.DrawLine(dash, inner.Left, my, inner.Right, my);
                }
                DrawSpark(g, inner, accent, top, 600, fill: true);

                // Corner brackets, instrument style.
                using var bracket = new Pen(Fade(Color.FromArgb(170, accent)), Math.Max(1f, S(1.2f)));
                float bl = S(9);
                foreach (var (cx, cy, dx, dy) in new[]
                         {
                             (rect.Left, rect.Top, 1f, 1f), (rect.Right, rect.Top, -1f, 1f),
                             (rect.Left, rect.Bottom, 1f, -1f), (rect.Right, rect.Bottom, -1f, -1f),
                         })
                {
                    g.DrawLine(bracket, cx, cy, cx + dx * bl, cy);
                    g.DrawLine(bracket, cx, cy, cx, cy + dy * bl);
                }
            }
            y += S(66);
        }

        // System view: smoothness dial, radar of the six vital signs, and readouts.
        y += S(16);
        const float BlockH = 132;
        if (g != null)
        {
            float load = m.GpuLoad, vram = m.VramPct, cpu = m.CpuLoad, ram = m.RamPct;
            int score = _inGame ? _advisor.Score : -1;

            float dialD = S(84), dialX = x0 + cw * 0.13f, dialY = y + S(48);
            DrawRing(g, dialX, dialY, dialD, S(6), score, score >= 0 ? ScoreColor(score) : accent,
                score >= 0 ? score.ToString() : "–", _fHuge!);
            string dialNote = score >= 80 ? "even pacing" : score >= 55 ? "some hitching" : score >= 0 ? "uneven pacing" : "no game running";
            Draw(g, "SMOOTHNESS", _fLabel!, Theme.Label, dialX - W("SMOOTHNESS", _fLabel!) / 2, dialY + dialD / 2 + S(12));
            Draw(g, dialNote, _fTiny!, Theme.Soft, dialX - W(dialNote, _fTiny!) / 2, dialY + dialD / 2 + S(26));

            DrawRadar(g, x0 + cw * 0.47f, y + S(64), S(50), accent, new (string, float)[]
            {
                ("GPU", load / 100f), ("VRAM", vram / 100f), ("GPU °C", (m.GpuTemp - 30f) / 65f),
                ("RAM", ram / 100f), ("CPU °C", (m.CpuTemp - 30f) / 65f), ("CPU", cpu / 100f),
            });

            float rx = x0 + cw * 0.70f, rw = cw * 0.30f, ry = y + S(2);
            Bar(g, rx, ry, rw, "GPU", Pct(load), load, accent);
            Bar(g, rx, ry + S(32), rw, "VRAM",
                float.IsNaN(vram) ? "–" : $"{m.VramUsedMB / 1024:0.0} / {m.VramTotalMB / 1024:0.0} GB", vram, Level(vram, accent));
            Bar(g, rx, ry + S(64), rw, "CPU",
                float.IsNaN(cpu) ? "–" : $"{cpu:0}%  ·  peak {m.CpuMaxThread:0}%", cpu, Level(cpu, accent));
            Bar(g, rx, ry + S(96), rw, "RAM", $"{m.RamUsedGB:0.0} / {m.RamTotalGB:0.0} GB", ram, Level(ram, accent));
        }
        y += S(BlockH);

        // Session timeline (left) and per-thread CPU load (right).
        y += S(4);
        var s0 = _session;
        float tlW = cw * 0.62f, thX = x0 + cw * 0.68f, thW = cw * 0.32f, stripH = S(34);
        if (g != null)
        {
            Draw(g, "SESSION TIMELINE", _fLabel!, Theme.Label, x0, y + S(10));
            Draw(g, "CPU THREADS", _fLabel!, Theme.Label, thX, y + S(10));
            float top = y + S(16), bottom = top + stripH;
            using (var baseline = new Pen(Fade(Theme.Line(34)), 1f))
            {
                g.DrawLine(baseline, x0, bottom, x0 + tlW, bottom);
                g.DrawLine(baseline, thX, bottom, thX + thW, bottom);
            }

            if (s0 != null && s0.Timeline.Count > 0)
            {
                // One bar per bucket: height is FPS, colour is how it compares with the session average.
                var tl = s0.Timeline;
                float avg = tl.Average(), max = Math.Max(tl.Max(), avg * 1.2f);
                int slots = Math.Max(tl.Count, 48);
                float bw = tlW / slots;
                for (int i = 0; i < tl.Count; i++)
                {
                    float v = tl[i], bh = Math.Max(S(2), stripH * Math.Min(1f, v / max));
                    Color c = v >= avg * 0.9f ? accent : v >= avg * 0.7f ? Theme.Warn : Theme.Hot;
                    using var b = new SolidBrush(Fade(Color.FromArgb(v >= avg * 0.9f ? 200 : 235, c)));
                    g.FillRectangle(b, x0 + i * bw, bottom - bh, Math.Max(1f, bw - S(1)), bh);
                }
                string span = Duration(tl.Count * s0.BucketSec);
                Draw(g, span, _fTiny!, Theme.Soft, x0 + tlW - W(span, _fTiny!), y + S(10));
            }
            else Draw(g, "fills in as you play", _fTiny!, Theme.Faint, x0, bottom - S(12));

            float[] loads = m.ThreadLoads;
            if (loads.Length > 0)
            {
                float bw = thW / loads.Length;
                for (int i = 0; i < loads.Length; i++)
                {
                    float bh = Math.Max(S(2), stripH * Math.Clamp(loads[i], 0, 100) / 100f);
                    using var b = new SolidBrush(Fade(Level(loads[i], accent)));
                    g.FillRectangle(b, thX + i * bw, bottom - bh, Math.Max(1f, bw - S(2)), bh);
                }
            }
        }
        y += S(16) + stripH + S(10);

        var s = _session;
        if (s?.LastHist is { Count: > 0 } h)
        {
            y += S(4);
            if (g != null)
            {
                var line = new List<Item>
                {
                    new() { Kind = K.Pair, A = "SESSION", B = Duration((int)h.Seconds), Color = Theme.Ink },
                    new() { Kind = K.Pair, A = "AVG", B = Num(h.AvgFps), Color = Theme.Ink },
                    new() { Kind = K.Pair, A = "1%", B = Num(h.LowFps(0.01)), Color = Theme.Ink },
                    new() { Kind = K.Pair, A = "0.1%", B = Num(h.LowFps(0.001)), Color = Theme.Ink },
                    new() { Kind = K.Pair, A = "STUTTERS", B = h.Stutters.ToString(), Color = Theme.Ink },
                };
                if (s.Previous != null)
                    line.Add(new() { Kind = K.Pair, A = "LAST TIME", B = Num(s.Previous.Avg), Color = Theme.Soft });
                DrawRow(g, line, x0, y, S(24));
            }
            y += S(26);
        }

        y += S(6);
        foreach (string text in PanelInsights())
        {
            if (g != null)
            {
                DrawDot(g, x0 + S(3.5f), y + S(9.5f), S(2.3f), accent);
                Draw(g, text, _fText!, Theme.Soft, x0 + S(13), y + S(14));
            }
            y += S(21);
        }

        y += S(6);
        if (g != null)
        {
            const string hint = "Ctrl+Alt+F11 close   ·   F10 benchmark   ·   F9 hide";
            Draw(g, hint, _fTiny!, Theme.Faint, x0 + (cw - W(hint, _fTiny!)) / 2, y + S(11));
        }
        return y + S(24);
    }

    IEnumerable<string> PanelInsights()
    {
        if (_inGame && _advisor.Insights.Count > 0) return _advisor.Insights;
        var list = new List<string>();
        foreach (var item in _sensors.Health.Where(i => i.Level >= 1).OrderByDescending(i => i.Level).Take(2))
            list.Add($"{item.Name}: {item.Value}. {char.ToUpper(item.Advice[0])}{item.Advice[1..]}.");
        if (_session != null) list.Add($"{_session.Name} is in the background. Switch back to see live numbers.");
        else list.Add("No game running. Statistics start on their own when one does.");
        var last = _lastRecord ?? History.All().LastOrDefault(r => r.Kind == "session");
        if (last != null)
            list.Add($"Last session: {last.Game}, {Duration(last.Seconds)}, {Num(last.Avg)} avg, {Num(last.Low1)} 1% low.");
        return list;
    }

    // ------------------------------------------------------------ theme body

    /// <summary>Outline of the body in the theme's shape. (0, 0) is the top-left of the body.</summary>
    GraphicsPath BodyPath(Shape shape, float w, float h, float ear, float r)
    {
        var path = new GraphicsPath();
        switch (shape)
        {
            case Shape.Pill:
                path.AddPath(Rounded(new RectangleF(0, 0, w, h), h <= S(60) ? h / 2f : r + S(2)), false);
                return path;
            case Shape.Blade:
            {
                float cl = Math.Min(S(6), h / 3f), cr = Math.Min(S(14), h / 2f);
                path.AddLines(new[]
                {
                    new PointF(-ear, 0), new PointF(0, ear), new PointF(0, h - cl), new PointF(cl, h),
                    new PointF(w - cr, h), new PointF(w, h - cr), new PointF(w, ear), new PointF(w + ear, 0),
                });
                path.CloseFigure();
                return path;
            }
            case Shape.Slab:
                ear *= 0.6f;
                r = Math.Min(S(3), h / 2f);
                break;
        }
        // Notch: flush with the screen edge, flared corners on top, round on the bottom.
        path.AddArc(-ear, 0, 2 * ear, 2 * ear, -90, 90);
        path.AddArc(ear, h - 2 * r, 2 * r, 2 * r, 180, -90);
        path.AddArc(w - ear - 2 * r, h - 2 * r, 2 * r, 2 * r, 90, -90);
        path.AddArc(w - ear, 0, 2 * ear, 2 * ear, 180, 90);
        path.CloseFigure();
        return path;
    }

    static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    static uint Hash(int x, int y)
    {
        uint v = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ 0x9E3779B9u;
        v ^= v >> 13; v *= 0x5bd1e995; v ^= v >> 15;
        return v;
    }

    TextureBrush? _grain;
    float _grainKey;

    /// <summary>Paints the theme's surface treatment inside the (already clipped) body.</summary>
    void DrawTexture(Graphics g, ThemeSpec t, float w, float h, float time)
    {
        float k = t.TextureStrength;
        int A(float a) => Math.Clamp((int)(a * k * Math.Max(_fade, 0.35f)), 0, 255);
        switch (t.Texture)
        {
            case Texture.Glass:
                using (var b = new LinearGradientBrush(new RectangleF(0, h * 0.5f, w, h * 0.5f + 1),
                           Color.FromArgb(0, 255, 255, 255), Color.FromArgb(A(16), 255, 255, 255), LinearGradientMode.Vertical))
                    g.FillRectangle(b, 0, h * 0.5f, w, h * 0.5f);
                break;

            case Texture.Scanlines:
            {
                float step = Math.Max(2f, S(3));
                using (var pen = new Pen(Color.FromArgb(A(11), t.Accent), Math.Max(1f, S(1))))
                    for (float y = 0; y < h; y += step) g.DrawLine(pen, 0, y, w, y);
                // Slow refresh band rolling down the screen.
                float band = S(22), y0 = (time * S(26)) % (h + band * 2) - band;
                using var bb = new LinearGradientBrush(new RectangleF(0, y0, w, band + 1), Color.Transparent, Color.Transparent, LinearGradientMode.Vertical)
                {
                    InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(0, t.Accent), Color.FromArgb(A(26), t.Accent), Color.FromArgb(0, t.Accent) },
                        Positions = new[] { 0f, 0.5f, 1f },
                    },
                };
                g.FillRectangle(bb, 0, y0, w, band);
                // Vignette toward the sides, like a curved tube.
                using var vig = new LinearGradientBrush(new RectangleF(0, 0, w, 1), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal)
                {
                    InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(A(70), 0, 0, 0), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(A(70), 0, 0, 0) },
                        Positions = new[] { 0f, 0.12f, 0.88f, 1f },
                    },
                };
                g.FillRectangle(vig, 0, 0, w, h);
                break;
            }

            case Texture.Grid:
            {
                float cell = S(11);
                using var fine = new Pen(Color.FromArgb(A(12), t.Accent), 1f);
                using var bold = new Pen(Color.FromArgb(A(22), t.Accent), 1f);
                int i = 0;
                for (float x = w / 2f % cell; x < w; x += cell, i++) g.DrawLine(i % 4 == 0 ? bold : fine, x, 0, x, h);
                i = 0;
                for (float y = cell; y < h; y += cell, i++) g.DrawLine(i % 4 == 3 ? bold : fine, 0, y, w, y);
                break;
            }

            case Texture.Horizon:
            {
                // Retro sun sinking behind the bottom edge, with its stripes.
                // Kept to a strip along the bottom so it never sits behind the numbers.
                float floor = Math.Min(h * 0.5f, S(24));
                float sr = Math.Min(floor * 1.25f, S(26)), scx = w / 2f, scy = h + sr * 0.3f;
                var sun = new RectangleF(scx - sr, scy - sr, 2 * sr, 2 * sr);
                using (var sb = new LinearGradientBrush(sun, Color.FromArgb(A(46), t.Accent3), Color.FromArgb(A(40), t.Accent), LinearGradientMode.Vertical))
                    g.FillEllipse(sb, sun);
                using (var cut = new SolidBrush(Color.FromArgb(A(220), t.BodyBottom)))
                    for (int i = 0; i < 5; i++)
                    {
                        float sy = scy - sr * 0.55f + i * sr * 0.16f, sh = S(0.8f) + i * S(0.7f);
                        g.FillRectangle(cut, scx - sr, sy, 2 * sr, sh);
                    }
                // Perspective grid on the floor, scrolling toward you.
                float hy = h - floor;
                using (var pen = new Pen(Color.FromArgb(A(40), t.Accent2), 1f))
                    for (int i = -24; i <= 24; i++)
                        g.DrawLine(pen, scx + i * S(9), hy, scx + i * S(40), h + S(4));
                float phase = time * 0.55f % 1f;
                for (int j = 0; j < 7; j++)
                {
                    float f = (j + phase) / 7f, y = hy + (h - hy) * f * f;
                    using var pen = new Pen(Color.FromArgb(A(10 + 40 * f), t.Accent2), 1f);
                    g.DrawLine(pen, 0, y, w, y);
                }
                // Haze along the horizon.
                using (var haze = new LinearGradientBrush(new RectangleF(0, hy - S(8), w, S(10)), Color.FromArgb(0, t.Accent), Color.FromArgb(A(28), t.Accent), LinearGradientMode.Vertical))
                    g.FillRectangle(haze, 0, hy - S(8), w, S(8));
                break;
            }

            case Texture.Carbon:
                using (var hatch = new HatchBrush(HatchStyle.DarkUpwardDiagonal, Color.FromArgb(A(9), t.Ink), Color.Transparent))
                    g.FillRectangle(hatch, 0, 0, w, h);
                // Hazard stripes tucked into the right corner.
                using (var stripe = new SolidBrush(Color.FromArgb(A(26), t.Accent)))
                {
                    float sw = S(4), x0 = w - S(64);
                    for (int i = 0; i < 6; i++)
                    {
                        float x = x0 + i * sw * 2.2f;
                        g.FillPolygon(stripe, new[] { new PointF(x, h), new PointF(x + sw, h), new PointF(x + sw + S(5), h - S(5)), new PointF(x + S(5), h - S(5)) });
                    }
                }
                break;

            case Texture.Hex:
            {
                float sz = S(8), hh = sz * 1.732f;
                using var pen = new Pen(Color.FromArgb(A(14), t.Accent), 1f);
                var hex = new PointF[6];
                int col = 0;
                for (float cx = 0; cx < w + sz; cx += sz * 1.5f, col++)
                    for (float cy = col % 2 == 0 ? 0 : hh / 2; cy < h + hh; cy += hh)
                    {
                        for (int i = 0; i < 6; i++)
                        {
                            double a = Math.PI / 3 * i;
                            hex[i] = new PointF(cx + sz * (float)Math.Cos(a), cy + sz * (float)Math.Sin(a));
                        }
                        g.DrawPolygon(pen, hex);
                    }
                // Fade it out toward the bottom so the numbers stay clean.
                using var fade = new LinearGradientBrush(new RectangleF(0, -1, w, h + 2), Color.FromArgb(0, t.BodyBottom), Color.FromArgb(200, t.BodyBottom), LinearGradientMode.Vertical);
                g.FillRectangle(fade, 0, 0, w, h);
                break;
            }

            case Texture.Aurora:
            {
                var blobs = new (Color c, float speed, float phase, float y)[]
                {
                    (t.Accent, 0.21f, 0f, 0.15f), (t.Accent2, 0.15f, 2.1f, 0.65f), (t.Accent3, 0.27f, 4.2f, 0.35f),
                };
                foreach (var (c, speed, phase, yk) in blobs)
                {
                    float bx = w * (0.5f + 0.42f * (float)Math.Sin(time * speed + phase));
                    float by = h * yk + (float)Math.Sin(time * speed * 1.7f + phase) * h * 0.25f;
                    float bw = Math.Max(S(120), w * 0.55f), bh = Math.Max(S(50), h * 2.2f);
                    using var bp = new GraphicsPath();
                    bp.AddEllipse(bx - bw / 2, by - bh / 2, bw, bh);
                    using var pb = new PathGradientBrush(bp) { CenterColor = Color.FromArgb(A(64), c), SurroundColors = new[] { Color.FromArgb(0, c) } };
                    g.FillPath(pb, bp);
                }
                // Faint vertical curtain folds.
                for (int i = 0; i < 9; i++)
                {
                    float fx = w * ((i * 0.137f + time * 0.012f) % 1f);
                    using var fold = new LinearGradientBrush(new RectangleF(fx - S(10), 0, S(20), 1), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal)
                    {
                        InterpolationColors = new ColorBlend
                        {
                            Colors = new[] { Color.FromArgb(0, t.Accent), Color.FromArgb(A(14), t.Accent), Color.FromArgb(0, t.Accent) },
                            Positions = new[] { 0f, 0.5f, 1f },
                        },
                    };
                    g.FillRectangle(fold, fx - S(10), 0, S(20), h);
                }
                break;
            }

            case Texture.Stars:
            {
                float cell = S(15);
                for (int ix = 0; ix * cell < w; ix++)
                    for (int iy = 0; iy * cell < h; iy++)
                    {
                        uint v = Hash(ix, iy);
                        if ((v & 0xFF) > 100) continue;
                        float px = ix * cell + ((v >> 8) & 0xFF) / 255f * cell;
                        float py = iy * cell + ((v >> 16) & 0xFF) / 255f * cell;
                        float size = S(0.5f + ((v >> 24) & 3) * 0.33f);
                        float tw = 0.5f + 0.5f * (float)Math.Sin(time * (0.8f + (v % 5) * 0.45f) + (v % 628) / 100f);
                        Color c = (v & 0x100) != 0 ? t.Accent3 : (v & 0x200) != 0 ? t.Accent2 : Color.White;
                        using var b = new SolidBrush(Color.FromArgb(A(40 + 110 * tw), c));
                        g.FillEllipse(b, px - size, py - size, size * 2, size * 2);
                        if (((v >> 24) & 3) == 3 && tw > 0.8f)
                            using (var pen = new Pen(Color.FromArgb(A(55 * tw), c), 1f))
                            {
                                g.DrawLine(pen, px - size * 2.2f, py, px + size * 2.2f, py);
                                g.DrawLine(pen, px, py - size * 2.2f, px, py + size * 2.2f);
                            }
                    }
                // A soft nebula cloud.
                using var np = new GraphicsPath();
                float nx = w * (0.3f + 0.05f * (float)Math.Sin(time * 0.1f));
                np.AddEllipse(nx - w * 0.3f, -h * 0.4f, w * 0.6f, h * 1.6f);
                using var nb = new PathGradientBrush(np) { CenterColor = Color.FromArgb(A(30), t.Accent), SurroundColors = new[] { Color.FromArgb(0, t.Accent) } };
                g.FillPath(nb, np);
                break;
            }

            case Texture.Embers:
            {
                // Heat rising off the bottom edge.
                using (var heat = new LinearGradientBrush(new RectangleF(0, h * 0.3f, w, h * 0.7f + 1), Color.FromArgb(0, t.Accent), Color.FromArgb(A(48), t.Accent), LinearGradientMode.Vertical))
                    g.FillRectangle(heat, 0, h * 0.3f, w, h * 0.7f);
                float cell = S(13);
                for (int ix = 0; ix * cell < w; ix++)
                    for (int n = 0; n < 2; n++)
                    {
                        uint v = Hash(ix, n + 7);
                        if ((v & 3) == 0) continue;
                        float speed = S(6 + (v >> 4) % 12), span = h + S(8);
                        float life = (time * speed + (v >> 8) % 1000 / 1000f * span) % span / span; // 0 at the bottom, 1 at the top
                        float py = h + S(2) - life * span;
                        float px = ix * cell + ((v >> 16) & 0xFF) / 255f * cell + (float)Math.Sin(time * 1.4f + v % 31) * S(3);
                        float size = S(0.6f + ((v >> 24) & 3) * 0.3f) * (1 - life * 0.5f);
                        Color c = Mix(t.Accent3, t.Accent2, life);
                        using var b = new SolidBrush(Color.FromArgb(A(220 * (1 - life)), c));
                        g.FillEllipse(b, px - size, py - size, size * 2, size * 2);
                    }
                break;
            }

            case Texture.Grain:
            {
                if (_grain == null || _grainKey != k)
                {
                    _grain?.Dispose();
                    var bmp = new Bitmap(96, 96, PixelFormat.Format32bppArgb);
                    var rnd = new Random(7);
                    int max = Math.Clamp((int)(16 * k), 1, 80);
                    for (int y = 0; y < 96; y++)
                        for (int x = 0; x < 96; x++)
                            bmp.SetPixel(x, y, rnd.Next(2) == 0 ? Color.FromArgb(rnd.Next(max), 255, 255, 255) : Color.FromArgb(rnd.Next(max), 0, 0, 0));
                    _grain = new TextureBrush(bmp);
                    _grainKey = k;
                    bmp.Dispose();
                }
                g.FillRectangle(_grain, 0, 0, w, h);
                break;
            }
        }
    }

    /// <summary>Draws the outline of the body in the theme's edge style. The flush top edge is left open.</summary>
    void DrawEdge(Graphics g, ThemeSpec t, GraphicsPath path, float w, float h, float ear, float r, int margin,
                  Color e1, Color e2, bool special, float time)
    {
        if (t.Edge == Edge.None && !special) return;
        var st = g.Save();
        if (t.Shape != Shape.Pill) g.SetClip(new RectangleF(-margin, 1.5f, w + 2 * margin, h + margin));
        float lw = Math.Max(1f, S(1.2f));
        var span = new RectangleF(-1, 0, w + 2, 1);
        LinearGradientBrush Grad(int alpha, Color a, Color b) => new(span, Color.FromArgb(alpha, a), Color.FromArgb(alpha, b), LinearGradientMode.Horizontal);

        switch (special && t.Edge is Edge.None or Edge.Shimmer ? Edge.Gradient : t.Edge)
        {
            case Edge.Neon:
            {
                using (var outer = Grad(110, e1, e2))
                using (var tube = new Pen(outer, Math.Max(2f, S(3.2f))) { LineJoin = LineJoin.Round })
                    g.DrawPath(tube, path);
                using var core = Grad(240, Mix(e1, Color.White, 0.6f), Mix(e2, Color.White, 0.6f));
                using var pen = new Pen(core, Math.Max(1f, S(1))) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, path);
                break;
            }
            case Edge.Shimmer:
            {
                float band = Math.Max(S(160), w * 0.7f);
                using var brush = new LinearGradientBrush(new RectangleF(0, 0, band, 1), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal)
                {
                    InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { t.Accent, t.Accent3, t.Accent2, Color.White, t.Accent },
                        Positions = new[] { 0f, 0.3f, 0.6f, 0.8f, 1f },
                    },
                    WrapMode = WrapMode.TileFlipX,
                };
                brush.TranslateTransform(time * S(70) % (band * 2), 0);
                using (var soft = Grad(60, t.Accent, t.Accent2))
                using (var softPen = new Pen(soft, Math.Max(2f, S(3.4f))) { LineJoin = LineJoin.Round })
                    g.DrawPath(softPen, path);
                using var pen = new Pen(brush, Math.Max(1f, S(1.5f))) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, path);
                break;
            }
            case Edge.Double:
            {
                using (var brush = Grad(220, e1, e2))
                using (var pen = new Pen(brush, lw))
                    g.DrawPath(pen, path);
                float d = S(3);
                using var inner = BodyPath(t.Shape, w - 2 * d, h - d, ear, Math.Max(S(2), r - d));
                using var m = new Matrix();
                m.Translate(d, 0);
                inner.Transform(m);
                using var ib = Grad(110, e2, e1);
                using var ip = new Pen(ib, Math.Max(1f, S(0.8f)));
                g.DrawPath(ip, inner);
                break;
            }
            case Edge.Dashed:
            {
                using var brush = Grad(220, e1, e2);
                using var pen = new Pen(brush, lw) { DashPattern = new[] { 4f, 2.5f }, DashOffset = -time * 4f };
                g.DrawPath(pen, path);
                break;
            }
            case Edge.Brackets:
            {
                using (var faint = new Pen(Color.FromArgb(60, e1), Math.Max(1f, S(1))))
                    g.DrawPath(faint, path);
                // Bright brackets on the lower corners only.
                float len = Math.Min(S(20), w * 0.15f);
                float by = Math.Max(h * 0.3f, h - len * 1.4f);
                var clip = new Region(new RectangleF(-margin, by, len + margin, h + margin));
                clip.Union(new RectangleF(w - len, by, len + margin, h + margin));
                clip.Union(new RectangleF(-margin, h - S(2.5f), w + 2 * margin, margin));
                clip.Exclude(new RectangleF(len, h - S(4), w - 2 * len, S(8)));
                g.SetClip(clip, CombineMode.Intersect);
                using (var glow = new Pen(Color.FromArgb(80, e1), Math.Max(2f, S(4))))
                    g.DrawPath(glow, path);
                using (var pen = new Pen(e2 == e1 ? e1 : Mix(e1, e2, 0.15f), Math.Max(1.5f, S(2))))
                    g.DrawPath(pen, path);
                clip.Dispose();
                break;
            }
            default:
            {
                using var brush = Grad(210, e1, e2);
                using var pen = new Pen(brush, lw);
                g.DrawPath(pen, path);
                break;
            }
        }
        g.Restore(st);
    }

    void Render()
    {
        if (!_shown || _fLabel == null) return;

        var theme = Theme.Current;
        int margin = (int)Math.Ceiling(S(16));
        int w = Math.Max(8, (int)Math.Ceiling(_w)), h = Math.Max(8, (int)Math.Ceiling(_h));
        float ear = S(Ear), r = Math.Min(S(_view == View.Panel ? 22 : 14), h / 2f);
        // A floating capsule hangs a few pixels below the screen edge.
        int drop = theme.Shape == Shape.Pill ? (int)Math.Ceiling(S(6)) : 0;
        Color accent = theme.Accent;

        using var bmp = new Bitmap(w + 2 * margin, h + margin + drop, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);
            g.TranslateTransform(margin, drop);

            using var path = BodyPath(theme.Shape, w, h, ear, r);

            Color accent2 = theme.Accent2;
            long nowTick = Environment.TickCount64;
            float time = nowTick / 1000f;
            float pulse = nowTick < _pulseUntil ? (_pulseUntil - nowTick) / (float)PulseMs : 0f;

            // Drop shadow for light and floating themes.
            if (theme.Shadow)
            {
                var st = g.Save();
                g.TranslateTransform(0, S(2.5f));
                for (int i = 6; i >= 1; i--)
                    using (var pen = new Pen(Color.FromArgb(theme.Light ? 9 : 16, 0, 0, 0), S(1.8f) * i) { LineJoin = LineJoin.Round })
                        g.DrawPath(pen, path);
                g.Restore(st);
            }

            // Glow around the notch: faint in the accent colour at rest, strong
            // for alerts, result cards and benchmark recording, and an amber
            // flash the instant a stutter lands.
            Color glow = accent;
            float strength = 14f * theme.Glow;
            bool alertView = (_view == View.Alert || _view == View.Wide) && _alert != null;
            if (alertView) { glow = _alert!.Color; strength = 56f * Math.Max(_fade, 0.5f); }
            else if (_view == View.Card || _view == View.Wide && _card != null) { strength = 50f * Math.Max(_fade, 0.5f); }
            else if (_session is { Benching: true } && _inGame) { glow = Theme.Hot; strength = 40f; }
            if (pulse > 0) { glow = Theme.Warn; strength = Math.Max(strength, 80f * pulse); }
            if (theme.Light) strength *= 0.6f;
            if (strength > 0)
                for (int i = 5; i >= 1; i--)
                    using (var pen = new Pen(Color.FromArgb((int)Math.Min(255f, strength / i), glow), S(2.4f) * i) { LineJoin = LineJoin.Round })
                        g.DrawPath(pen, path);

            // Body: tinted glass, top to bottom.
            int bgAlpha = (int)(255 * Math.Clamp(_config.Opacity, 40, 100) / 100f);
            if (_view == View.Panel || _view == View.Intro) bgAlpha = Math.Max(bgAlpha, 252); // text-heavy views stay readable
            using (var back = new LinearGradientBrush(new RectangleF(0, -1, w, h + 2),
                       Color.FromArgb(bgAlpha, theme.BodyTop), Color.FromArgb(bgAlpha, theme.BodyBottom), LinearGradientMode.Vertical))
                g.FillPath(back, path);

            // Surface: texture, then a glass highlight.
            var inside = g.Save();
            g.SetClip(path);
            bool moving = _config.Motion && _config.Animate;
            DrawTexture(g, theme, w, h, moving ? time : 0f);
            if (theme.Sheen)
            {
                float sh = Math.Min(h * 0.55f, S(26));
                using var sheen = new LinearGradientBrush(new RectangleF(0, -1, w, sh + 1),
                    Color.FromArgb(theme.Light ? 120 : 26, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical);
                g.FillRectangle(sheen, 0, 0, w, sh);
            }
            g.Restore(inside);

            // Edge.
            Color e1 = pulse > 0 ? Theme.Warn : alertView ? _alert!.Color : accent;
            Color e2 = pulse > 0 ? Theme.Warn : alertView ? _alert!.Color : accent2;
            bool special = pulse > 0 || alertView;
            DrawEdge(g, theme, path, w, h, ear, r, margin, e1, e2, special, moving ? time : 0f);

            g.SetClip(path);

            // A small luminous mark centred on the bottom edge.
            if (theme.CenterMark && _view != View.Boot)
            {
                float mw = Math.Min(S(34), w * 0.2f), my = h - S(2.5f);
                using (var halo = new Pen(Fade(Color.FromArgb(70, e1)), S(4)))
                    g.DrawLine(halo, w / 2f - mw / 2, my, w / 2f + mw / 2, my);
                using (var core = new Pen(Fade(e1), Math.Max(1f, S(1.6f))))
                    g.DrawLine(core, w / 2f - mw / 2, my, w / 2f + mw / 2, my);
            }

            // A light sweeps along the bottom edge whenever the notch changes form.
            if (_fade < 1)
            {
                float sx = _fade * w, half = S(46);
                using var sweep = new LinearGradientBrush(new RectangleF(sx - half, 0, 2 * half, 1),
                    Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal)
                {
                    InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(0, theme.Sweep), Color.FromArgb(230, theme.Sweep), Color.FromArgb(0, theme.Sweep) },
                        Positions = new[] { 0f, 0.5f, 1f },
                    },
                };
                using var sweepPen = new Pen(sweep, S(2f));
                g.DrawLine(sweepPen, sx - half + 1, h - S(1), sx + half - 1, h - S(1));
            }

            float rowW = RowWidth(_row);
            if (_view == View.Wide)
            {
                float side = S(Ear) + S(22);
                float leftW = RowWidth(_row), midW = RowWidth(_mid), rightW = RowWidth(_right);
                float rightX = w - side - rightW;
                DrawRow(g, _row, side, 0, h);
                // The centre group sits midway in the space between the left and right groups.
                if (_mid.Count > 0) DrawRow(g, _mid, side + leftW + (rightX - side - leftW - midW) / 2f, 0, h);
                DrawRow(g, _right, rightX, 0, h);
            }
            else if (_view == View.Panel)
            {
                DrawRow(g, _row, (w - rowW) / 2f, 0, S(32));
                PanelLayout(g, w);
            }
            else if (_view == View.Card && _cardNote.Length > 0)
            {
                DrawRow(g, _row, (w - rowW) / 2f, 0, S(32));
                Draw(g, _cardNote, _fText!, Theme.Soft, (w - W(_cardNote, _fText!)) / 2f, S(45));
            }
            else if (_view == View.Intro && _session is { Checks: { } checks } intro && intro.ChecksShown(nowTick) > 0)
            {
                // Pre-flight checklist: a tick or a warning mark, then the finding.
                DrawRow(g, _row, (w - rowW) / 2f, 0, S(34));
                int shown = intro.ChecksShown(nowTick);
                float lx = S(Ear) + S(20), ly = S(36);
                for (int i = 0; i < shown; i++, ly += S(21))
                {
                    Check c = checks[i];
                    float my = ly + S(10);
                    if (c.Ok)
                    {
                        using var pen = new Pen(Fade(Theme.Good), S(1.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                        g.DrawLines(pen, new[] { new PointF(lx, my), new PointF(lx + S(3.5f), my + S(3.5f)), new PointF(lx + S(10), my - S(4)) });
                    }
                    else
                    {
                        using var pen = new Pen(Fade(Theme.Warn), S(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        g.DrawLine(pen, lx + S(5), my - S(5), lx + S(5), my + S(1));
                        using var dot = new SolidBrush(Fade(Theme.Warn));
                        g.FillEllipse(dot, lx + S(3.8f), my + S(3.2f), S(2.4f), S(2.4f));
                    }
                    Draw(g, c.Text, _fText!, c.Ok ? Theme.Soft : Theme.Ink, lx + S(20), ly + S(14.5f));
                }
            }
            else DrawRow(g, _row, (w - rowW) / 2f, 0, h);
        }

        Rectangle screen = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
        int px = _view == View.Wide ? screen.Left + (screen.Width - w) / 2 : _config.Position switch
        {
            "Left" => screen.Left + (int)S(40),
            "Right" => screen.Right - w - (int)S(40),
            _ => screen.Left + (screen.Width - w) / 2,
        };
        if (_capture != null) _capture(bmp);
        else Push(bmp, px - margin, screen.Top);
    }

    void Push(Bitmap bmp, int x, int y)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var dst = new POINT { x = x, y = y };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ================================================================ tray

    void BuildTray()
    {
        var menu = new ContextMenuStrip();
        var show = new ToolStripMenuItem("Show notch  (Ctrl+Alt+F9)");
        show.Click += (_, _) => ToggleShown();
        var panel = new ToolStripMenuItem("Details panel  (Ctrl+Alt+F11)");
        panel.Click += (_, _) => TogglePanel();
        var always = new ToolStripMenuItem("Always on: FPS for any app");
        always.Click += (_, _) => ToggleAlwaysOn();
        var wide = new ToolStripMenuItem("Wide detail bar  (Ctrl+Alt+F8)");
        wide.Click += (_, _) => ToggleWide();
        var bench = new ToolStripMenuItem("Start / stop benchmark  (Ctrl+Alt+F10)");
        bench.Click += (_, _) => ToggleBenchmark();
        var themes = new ToolStripMenuItem("Theme");
        foreach (string name in Theme.AccentNames)
        {
            var item = new ToolStripMenuItem(name) { ToolTipText = Theme.Get(name).About };
            item.Click += (_, _) => { _config.Accent = name; ApplyConfig(); };
            themes.DropDownItems.Add(item);
        }
        var settings = new ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => OpenSettings();
        var history = new ToolStripMenuItem("History…");
        history.Click += (_, _) => OpenHistory();
        var live = new ToolStripMenuItem("What is happening now…");
        live.Click += (_, _) => OpenLive();
        var report = new ToolStripMenuItem("Last session report…");
        report.Click += (_, _) =>
        {
            var last = _lastRecord ?? History.All().LastOrDefault(r => r.Kind == "session");
            if (last != null) new ReportForm(last).Show();
            else ShowAlert(new Alert("report", "No sessions yet", "Play a game for over a minute first", Theme.Label), 2500);
        };
        var hardware = new ToolStripMenuItem("Hardware health…");
        hardware.Click += (_, _) => OpenHealth();
        var update = new ToolStripMenuItem("Check for updates…");
        update.Click += (_, _) =>
        {
            if (!_updater.Available) { _updater.CheckSoon(manual: true); return; }
            string notes = _updater.Notes is { Length: > 0 } n ? "\n\n" + Clip(n.Replace("\r", ""), 700) : "";
            var answer = MessageBox.Show(
                $"Install NotchHUD {_updater.LatestTag}? You have {Updater.CurrentText}.\n\n" +
                "NotchHUD closes, updates itself and starts again. Settings and history are kept." + notes,
                "NotchHUD update", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer != DialogResult.Yes) return;
            ShowAlert(new Alert("update", "Downloading update", _updater.LatestTag ?? "", Theme.Accent(_config.Accent)), 60000);
            _updater.Install(this, Close);
        };
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Close();

        menu.Opening += (_, _) =>
        {
            foreach (ToolStripMenuItem t in themes.DropDownItems) t.Checked = t.Text == Theme.Current.Name;
            show.Checked = _shown; panel.Checked = _panel; wide.Checked = _config.Wide; always.Checked = _config.AlwaysOn;
            update.Enabled = !_updater.Busy;
            update.Text = _updater.State == "downloading" ? "Downloading update…"
                : _updater.Busy ? "Checking for updates…"
                : _updater.Available ? $"Install update {_updater.LatestTag}…"
                : $"Check for updates…  (v{Updater.CurrentText})";
            update.Font = new Font(update.Font, _updater.Available ? FontStyle.Bold : FontStyle.Regular);
        };
        menu.Items.AddRange(new ToolStripItem[]
            { show, wide, always, panel, bench, new ToolStripSeparator(), themes, live, hardware, report, settings, history, new ToolStripSeparator(), update, exit });

        _tray.Icon = MakeIcon();
        _tray.Text = "NotchHUD";
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.Visible = true;
    }

    void OpenLive()
    {
        if (_liveForm is { IsDisposed: false }) { _liveForm.Activate(); return; }
        _liveForm = new LiveForm(_sensors, _net);
        _liveForm.Show();
    }

    void OpenHealth()
    {
        if (_healthForm is { IsDisposed: false }) { _healthForm.Activate(); return; }
        _healthForm = new HealthForm(_sensors, _events);
        _healthForm.Show();
    }

    void OpenSettings()
    {
        if (_settings is { IsDisposed: false }) { _settings.Activate(); return; }
        _settings = new SettingsForm(_config, ApplyConfig);
        _settings.Show();
    }

    void OpenHistory()
    {
        if (_history is { IsDisposed: false }) { _history.Activate(); return; }
        _history = new HistoryForm();
        _history.Show();
    }

    void ApplyConfig()
    {
        _config.Save();
        if (!_shown) return;
        UpdateLayout(Environment.TickCount64);
        Render();
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Rounded(new RectangleF(1, 8, 30, 16), 8);
            g.FillPath(Brushes.Black, path);
            using var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 1.5f);
            g.DrawPath(pen, path);
            using var dot = new SolidBrush(Theme.Good);
            g.FillEllipse(dot, 7, 13, 6, 6);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    void ToggleShown()
    {
        _shown = !_shown;
        if (_shown) { Show(); Tick(); }
        else { _animTimer.Stop(); Hide(); }
    }

    void ToggleAlwaysOn()
    {
        _config.AlwaysOn = !_config.AlwaysOn;
        _config.Save();
        if (!_config.AlwaysOn && _session is { Casual: true }) EndSession();
        ShowAlert(new Alert("always", _config.AlwaysOn ? "Always on" : "Games only",
            _config.AlwaysOn ? "FPS for any app that is drawing frames" : "FPS only for games", Theme.Accent(_config.Accent)), 2500);
    }

    void ToggleWide()
    {
        _config.Wide = !_config.Wide;
        _config.Save();
        if (!_shown) { _shown = true; Show(); }
        Tick();
    }

    void TogglePanel()
    {
        _panel = !_panel;
        if (!_shown) { _shown = true; Show(); }
        Tick();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _dataTimer.Stop();
        _animTimer.Stop();
        try { if (_session != null) EndSession(); } catch { }
        UnregisterHotKey(Handle, 1);
        UnregisterHotKey(Handle, 2);
        UnregisterHotKey(Handle, 3);
        UnregisterHotKey(Handle, 4);
        _tray.Visible = false;
        _tray.Dispose();
        _frames.Dispose();
        _sensors.Dispose();
        _events.Dispose();
        _net.Dispose();
        base.OnFormClosed(e);
    }

    // ============================================================== native

    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
}
