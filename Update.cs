using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace NotchHUD;

/// <summary>
/// Checks GitHub for a newer release and installs it: downloads the zip,
/// unpacks it next to the data folder, then a small script waits for
/// NotchHUD to exit, copies the new files over the program folder and
/// starts it again. Settings and history live in %LOCALAPPDATA% and are
/// never touched.
/// </summary>
sealed class Updater
{
    const string Repo = "cagatay-softgineer/NotchHUD";
    static readonly HttpClient Http = MakeClient();

    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));
    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    // Set from a worker thread, read on the UI thread.
    public volatile string? LatestTag;     // e.g. "v1.0.2" when newer than Current
    public volatile string? AssetUrl;
    public volatile string? ReleaseUrl;
    public volatile string? Notes;
    public volatile string State = "";     // "", "checking", "downloading", "error: …"
    volatile bool _announce;               // a newer version was found and not yet shown
    volatile bool _reportLatest;           // a manual check found nothing new
    long _lastCheck = long.MinValue / 2;

    public bool Available => LatestTag != null && AssetUrl != null;
    public bool Busy => State is "checking" or "downloading";

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NotchHUD-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static Version? ParseTag(string tag) =>
        Version.TryParse(tag.TrimStart('v', 'V').Split('-', '+')[0], out var v) ? Normalize(v) : null;

    /// <summary>Checks at most every <paramref name="everyMs"/> unless forced.</summary>
    public void CheckSoon(bool manual, long everyMs = 6 * 3600_000)
    {
        long now = Environment.TickCount64;
        if (Busy || (!manual && now - _lastCheck < everyMs)) return;
        _lastCheck = now;
        State = "checking";
        Task.Run(async () =>
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
                var root = doc.RootElement;
                string tag = root.GetProperty("tag_name").GetString() ?? "";
                var v = ParseTag(tag);
                string? asset = null;
                foreach (var a in root.GetProperty("assets").EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        asset = a.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
                if (v != null && v > Current && asset != null)
                {
                    bool fresh = LatestTag != tag;
                    AssetUrl = asset;
                    ReleaseUrl = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
                    Notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
                    LatestTag = tag;
                    if (fresh || manual) _announce = true;
                    Log.Write($"update: {tag} available (running {CurrentText})");
                }
                else
                {
                    LatestTag = null;
                    if (manual) _reportLatest = true;
                }
                State = "";
            }
            catch (Exception ex)
            {
                State = "error: " + (ex is HttpRequestException or TaskCanceledException ? "could not reach GitHub" : ex.Message);
                Log.Write("update check failed: " + ex.Message);
                if (manual) _reportLatest = true;
            }
        });
    }

    /// <summary>True once per newly found version (or per manual check that found one).</summary>
    public bool TakeAnnouncement() { if (!_announce) return false; _announce = false; return true; }

    /// <summary>True once after a manual check that found no newer version (or failed).</summary>
    public bool TakeUpToDate() { if (!_reportLatest) return false; _reportLatest = false; return true; }

    /// <summary>
    /// Downloads and unpacks the new version, then calls <paramref name="exit"/>
    /// on the UI thread so the installer script can replace the files.
    /// </summary>
    public void Install(Control ui, Action exit)
    {
        if (!Available || Busy) return;
        string url = AssetUrl!, tag = LatestTag!;
        State = "downloading";
        Task.Run(async () =>
        {
            try
            {
                string work = Path.Combine(Log.Folder, "update");
                if (Directory.Exists(work)) Directory.Delete(work, true);
                Directory.CreateDirectory(work);
                string zip = Path.Combine(work, "update.zip"), files = Path.Combine(work, "files");
                await using (var src = await Http.GetStreamAsync(url))
                await using (var dst = File.Create(zip))
                    await src.CopyToAsync(dst);
                ZipFile.ExtractToDirectory(zip, files);
                if (!File.Exists(Path.Combine(files, "NotchHUD.exe")))
                    throw new InvalidDataException("the download does not contain NotchHUD.exe");

                string target = AppContext.BaseDirectory.TrimEnd('\\');
                string exe = Path.Combine(target, "NotchHUD.exe");
                string script = Path.Combine(work, "install.ps1");
                string Q(string s) => "'" + s.Replace("'", "''") + "'";
                File.WriteAllText(script, string.Join("\r\n", new[]
                {
                    "$ErrorActionPreference = 'Stop'",
                    $"$log = {Q(Path.Combine(work, "install.log"))}",
                    $"try {{ Wait-Process -Id {Environment.ProcessId} -Timeout 30 -ErrorAction SilentlyContinue }} catch {{ }}",
                    "Start-Sleep -Milliseconds 500",
                    "try {",
                    $"  Copy-Item -Path (Join-Path {Q(files)} '*') -Destination {Q(target)} -Recurse -Force",
                    $"  'installed {tag}' | Out-File $log",
                    "} catch { $_ | Out-File $log }",
                    $"Start-Process -FilePath {Q(exe)}",
                }));

                Log.Write($"update: installing {tag} into {target}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                State = "";
                ui.BeginInvoke(exit);
            }
            catch (Exception ex)
            {
                State = "error: " + ex.Message;
                Log.Write("update install failed: " + ex);
                _reportLatest = true;
            }
        });
    }
}
