using System.Diagnostics;
using System.Text.Json;

namespace NotchHUD;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Developer aid: render every theme to PNG files and exit (no sensors, no overlay).
        if (args.Length >= 2 && args[0] == "--preview")
        {
            ApplicationConfiguration.Initialize();
            NotchWindow.RenderPreviews(args[1]);
            return;
        }

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
    public bool Motion { get; set; } = true;         // living theme effects: aurora, shimmer, scanlines, stars
    public bool Voice { get; set; } = false;         // speak alerts
    public bool NowPlaying { get; set; } = true;     // show the Spotify track
    public bool HealthWatch { get; set; } = true;    // alert on throttling and failing hardware, in and out of games
    public bool NetWatch { get; set; } = true;       // alert on lag spikes and packet loss, and say whether it is the router or the internet
    public bool Wide { get; set; } = false;          // full-width detail bar across the top of the screen
    public bool FullscreenFix { get; set; } = true;  // keep the notch visible over OpenGL/Vulkan exclusive fullscreen (Minecraft)
    public bool AlwaysOn { get; set; } = false;      // measure FPS for any app drawing frames, not just games
    public bool CheckUpdates { get; set; } = true;   // look for a newer release on GitHub every few hours
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
