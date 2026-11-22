using System.Diagnostics;
using System.Text.Json;

namespace NotchHUD;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(false, "NotchHUD.SingleInstance");
        bool owned;
        try { owned = mutex.WaitOne(3000); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) return;

        Log.Start();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled: " + e.ExceptionObject);
        Application.ThreadException += (_, e) => Log.Write("UI exception: " + e.Exception);

        // Never compete with the game for CPU time.
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        ApplicationConfiguration.Initialize();
        Application.Run(new NotchWindow());
    }
}

static class Log
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotchHUD");
    static readonly string File = Path.Combine(Dir, "log.txt");
    static readonly object Gate = new();

    public static string Folder => Dir;

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            System.IO.File.WriteAllText(File, $"{DateTime.Now:u} NotchHUD started{Environment.NewLine}");
        }
        catch { }
    }

    public static void Write(string line)
    {
        try
        {
            lock (Gate) System.IO.File.AppendAllText(File, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { }
    }
}

static class Theme
{
    public static readonly Color Label = Color.FromArgb(150, 152, 160);
    public static readonly Color Soft = Color.FromArgb(214, 216, 222);
    public static readonly Color Good = Color.FromArgb(120, 222, 150);
    public static readonly Color Warn = Color.FromArgb(255, 200, 92);
    public static readonly Color Hot = Color.FromArgb(255, 104, 96);

    public static readonly string[] AccentNames =
        { "Ice", "Mint", "Violet", "Amber", "Rose", "White", "Synthwave", "Matrix", "Stealth" };

    /// <summary>Tint of the notch body, top and bottom of its gradient.</summary>
    public static (Color top, Color bottom) Body(string name) => name switch
    {
        "Synthwave" => (Color.FromArgb(40, 14, 50), Color.FromArgb(9, 3, 16)),
        "Matrix" => (Color.FromArgb(5, 24, 11), Color.FromArgb(0, 4, 1)),
        "Stealth" => (Color.FromArgb(18, 18, 20), Color.FromArgb(4, 4, 5)),
        "Amber" => (Color.FromArgb(26, 19, 10), Color.FromArgb(5, 3, 1)),
        _ => (Color.FromArgb(17, 20, 27), Color.FromArgb(2, 3, 5)),
    };

    /// <summary>How strong the resting glow around the notch is.</summary>
    public static float Glow(string name) => name switch { "Synthwave" => 2.2f, "Matrix" => 1.6f, "Stealth" => 0f, _ => 1f };

    /// <summary>Typeface for numbers and labels (regular, bold).</summary>
    public static (string regular, string bold) NumberFont(string name) => name == "Matrix"
        ? ("Cascadia Mono", "Cascadia Mono SemiBold")
        : ("Bahnschrift", "Bahnschrift SemiBold");

    public static Color Accent(string name) => name switch
    {
        "Synthwave" => Color.FromArgb(255, 96, 208),
        "Matrix" => Color.FromArgb(74, 246, 128),
        "Stealth" => Color.FromArgb(204, 208, 216),
        "Mint" => Color.FromArgb(110, 231, 183),
        "Violet" => Color.FromArgb(167, 139, 250),
        "Amber" => Color.FromArgb(251, 191, 36),
        "Rose" => Color.FromArgb(251, 113, 133),
        "White" => Color.FromArgb(235, 235, 240),
        _ => Color.FromArgb(96, 210, 255),
    };

    /// <summary>Second colour of the neon gradient that pairs with an accent.</summary>
    public static Color Accent2(string name) => name switch
    {
        "Synthwave" => Color.FromArgb(96, 210, 255),
        "Matrix" => Color.FromArgb(20, 170, 80),
        "Stealth" => Color.FromArgb(120, 124, 132),
        "Mint" => Color.FromArgb(96, 210, 255),
        "Violet" => Color.FromArgb(251, 113, 133),
        "Amber" => Color.FromArgb(251, 113, 133),
        "Rose" => Color.FromArgb(167, 139, 250),
        "White" => Color.FromArgb(96, 210, 255),
        _ => Color.FromArgb(167, 139, 250),
    };
}

sealed class Config
{
    public string Position { get; set; } = "Center"; // Left, Center, Right
    public double Scale { get; set; } = 1.0;
    public int Opacity { get; set; } = 94;           // background opacity, percent
    public string Accent { get; set; } = "Ice";
    public bool ShowGraph { get; set; } = true;
    public bool Alerts { get; set; } = true;
    public bool Intro { get; set; } = true;
    public bool Summary { get; set; } = true;
    public bool Optimizer { get; set; } = true;      // game priority up, background apps in efficiency mode
    public bool Animate { get; set; } = true;        // boot animation and counting numbers
    public bool Voice { get; set; } = false;         // speak alerts
    public bool NowPlaying { get; set; } = true;     // show the Spotify track
    public bool HealthWatch { get; set; } = true;    // alert on throttling and failing hardware, in and out of games
    public bool NetWatch { get; set; } = true;       // alert on lag spikes and packet loss, and say whether it is the router or the internet
    public bool Wide { get; set; } = false;          // full-width detail bar across the top of the screen
    public bool EventWatch { get; set; } = true;    // alert on important Windows errors (WHEA, blue screens, disk, driver, crashes)

    static string FilePath => Path.Combine(Log.Folder, "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
        }
        catch { }
        return new Config();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}

/// <summary>Start-with-Windows through a scheduled task, so it launches elevated without a prompt.</summary>
static class Autostart
{
    const string TaskName = "NotchHUD";

    public static bool IsEnabled() => Run($"/Query /TN \"{TaskName}\"") == 0;

    public static void Set(bool on)
    {
        if (on)
            Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{Environment.ProcessPath}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        else
            Run($"/Delete /TN \"{TaskName}\" /F");
    }

    static int Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            if (p == null) return -1;
            p.WaitForExit(5000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
