using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NotchHUD;

/// <summary>
/// Auto-optimizer: acts instead of advising. While a game is in the foreground
/// it gives the game a high CPU priority and puts browsers, launchers and
/// similar background programs into Windows efficiency mode (lowest priority
/// plus power throttling), and trims their memory when RAM is tight. Nothing
/// is closed or suspended, so launchers the game depends on keep working.
/// Everything is put back when the game loses focus or closes.
/// </summary>
sealed class Optimizer
{
    static readonly HashSet<string> Background = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "msedgewebview2",
        "EpicGamesLauncher", "EpicWebHelper", "steamwebhelper", "RiotClientServices", "RiotClientUx",
        "RiotClientCrashHandler", "Launcher", "LauncherPatcher", "SocialClubHelper", "RockstarService",
        "GalaxyClient", "GalaxyClient Helper", "upc", "UbisoftConnect", "EADesktop", "EABackgroundService",
        "Battle.net", "CurseForge", "Modrinth App", "OneDrive", "fdm", "SamsungMagician", "GoogleDriveFS",
        "Dropbox", "Teams", "ms-teams", "slack", "WhatsApp", "Telegram", "Code", "PhoneExperienceHost",
        "Widgets", "WidgetService", "logioptionsplus_agent", "remoting_host",
    };

    // Never touched, even when busy: voice and music (audio would crackle),
    // capture, cooling control, anti-cheat, and Windows itself.
    static readonly HashSet<string> Untouchable = new(StringComparer.OrdinalIgnoreCase)
    {
        "Discord", "TeamSpeak", "Spotify", "obs64", "steam", "explorer", "svchost", "lsass", "services", "wininit",
        "winlogon", "MsMpEng", "FanControl", "NZXT CAM", "audiodg", "vgc", "EasyAntiCheat", "BEService", "claude",
    };

    const uint PROCESS_SET_QUOTA = 0x0100, PROCESS_SET_INFORMATION = 0x0200, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint NORMAL = 0x20, IDLE = 0x40, HIGH = 0x80;
    const int ProcessPowerThrottling = 4;
    const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a", HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottling { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool SetPriorityClass(IntPtr h, uint cls);
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int cls, ref PowerThrottling info, uint size);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);

    readonly object _gate = new();
    readonly Dictionary<int, uint> _calmed = new(); // pid -> original priority class
    int _gamePid, _session;
    uint _gameOriginal;
    string? _previousPlan;

    /// <summary>Background processes currently held in efficiency mode.</summary>
    public int Active { get { lock (_gate) return _calmed.Count; } }

    public void GameStarted(int pid)
    {
        lock (_gate)
        {
            _session = pid;
            IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                uint original = GetPriorityClass(h);
                if (original == NORMAL && SetPriorityClass(h, HIGH))
                {
                    _gamePid = pid;
                    _gameOriginal = original;
                    Log.Write("optimizer: game priority set to High");
                }
                CloseHandle(h);
            }
            else Log.Write("optimizer: the game does not allow its priority to be changed");

            // A power-saver plan throttles the CPU; switch away from it for the session.
            try
            {
                string active = Run("powercfg", "/getactivescheme");
                if (active.Contains(PowerSaver, StringComparison.OrdinalIgnoreCase))
                {
                    _previousPlan = PowerSaver;
                    Run("powercfg", "/setactive " + HighPerformance);
                    Log.Write("optimizer: power plan switched from Power saver to High performance");
                }
            }
            catch { }
        }
    }

    /// <summary>Puts the known background programs into efficiency mode. Returns how many it changed.</summary>
    public int Engage(int gamePid, float ramPct)
    {
        int changed = 0;
        lock (_gate)
        {
            if (gamePid != _session) return 0; // the game already closed; nothing may be calmed after that
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != gamePid && Background.Contains(p.ProcessName) && !_calmed.ContainsKey(p.Id)
                        && Apply(p.Id, trim: ramPct >= 75))
                        changed++;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        if (changed > 0) Log.Write($"optimizer: {changed} background processes moved to efficiency mode");
        return changed;
    }

    /// <summary>Calms one program that keeps eating CPU. True when it actually changed something.</summary>
    public bool Calm(int pid, string name)
    {
        if (pid <= 4 || Untouchable.Contains(name)) return false;
        lock (_gate)
        {
            if (_session == 0 || _calmed.ContainsKey(pid)) return false;
            try
            {
                using var p = Process.GetProcessById(pid);
                string path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { }
                if (path.Length == 0 || path.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { return false; }
            return Apply(pid, trim: false);
        }
    }

    bool Apply(int pid, bool trim)
    {
        IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            uint original = GetPriorityClass(h);
            if (original != NORMAL) return false; // leave anything that manages its own priority alone
            if (!SetPriorityClass(h, IDLE)) return false;
            var on = new PowerThrottling { Version = 1, ControlMask = 1, StateMask = 1 };
            SetProcessInformation(h, ProcessPowerThrottling, ref on, (uint)Marshal.SizeOf<PowerThrottling>());
            if (trim) EmptyWorkingSet(h);
            _calmed[pid] = original;
            return true;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Returns every background program to how it was.</summary>
    public void Release()
    {
        lock (_gate)
        {
            foreach (var (pid, original) in _calmed)
            {
                IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) continue;
                SetPriorityClass(h, original);
                var off = new PowerThrottling { Version = 1, ControlMask = 0, StateMask = 0 };
                SetProcessInformation(h, ProcessPowerThrottling, ref off, (uint)Marshal.SizeOf<PowerThrottling>());
                CloseHandle(h);
            }
            if (_calmed.Count > 0) Log.Write($"optimizer: restored {_calmed.Count} background processes");
            _calmed.Clear();
        }
    }

    public void GameEnded()
    {
        lock (_gate) _session = 0;
        Release();
        lock (_gate)
        {
            if (_gamePid != 0)
            {
                IntPtr h = OpenProcess(PROCESS_SET_INFORMATION, false, _gamePid);
                if (h != IntPtr.Zero) { SetPriorityClass(h, _gameOriginal); CloseHandle(h); }
                _gamePid = 0;
            }
            if (_previousPlan != null)
            {
                try { Run("powercfg", "/setactive " + _previousPlan); } catch { }
                _previousPlan = null;
            }
        }
    }

    static string Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
        });
        if (p == null) return "";
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(4000);
        return output;
    }
}
