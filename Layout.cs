namespace NotchHUD;

/// <summary>A place on screen whose metrics can be chosen and ordered.</summary>
enum Surface { Game, Idle, WideGame, WideSystem }

/// <summary>
/// One metric that can be shown on a surface. Group decides where separators
/// go: a separator is drawn between neighbouring modules of different groups,
/// except next to a "tail" module (status chips sit right after what they describe).
/// </summary>
sealed record Module(string Id, string Name, string Group);

/// <summary>One entry of a layout: a module and whether it is shown.</summary>
sealed class Slot
{
    public string Id { get; set; } = "";
    public bool On { get; set; } = true;

    public Slot() { }
    public Slot(string id, bool on) { Id = id; On = on; }
}

static class Modules
{
    public static string SurfaceName(Surface s) => s switch
    {
        Surface.Game => "Game notch",
        Surface.Idle => "Desktop notch (no game)",
        Surface.WideGame => "Wide bar: game side",
        _ => "Wide bar: system side",
    };

    /// <summary>Modules each surface offers, in their default order, with whether they start on.</summary>
    static readonly Dictionary<Surface, (Module m, bool on)[]> Catalog = new()
    {
        [Surface.Game] = new (Module, bool)[]
        {
            (new("icon", "Game icon", "game"), true),
            (new("graph", "Frame-time graph", "game"), true),
            (new("fps", "FPS", "game"), true),
            (new("avg", "Average FPS", "game"), true),
            (new("low1", "1% low", "game"), true),
            (new("low01", "0.1% low", "game"), true),
            (new("frametime", "Frame time (ms)", "game"), false),
            (new("score", "Smoothness ring", "game"), true),
            (new("stutter", "Stutters in the last 10 s", "game"), false),
            (new("cputemp", "CPU temperature", "system"), true),
            (new("gputemp", "GPU temperature", "system"), true),
            (new("cpuload", "CPU load meter", "load"), false),
            (new("gpuload", "GPU load meter", "load"), false),
            (new("vram", "VRAM meter", "load"), false),
            (new("ram", "RAM meter", "load"), false),
            (new("ping", "Ping", "system"), true),
            (new("clock", "Clock", "extra"), false),
            (new("session", "Session time", "extra"), false),
            (new("status", "Status chip (bottleneck, scene, throttling, recording)", "tail"), true),
        },
        [Surface.Idle] = new (Module, bool)[]
        {
            (new("clock", "Clock", "clock"), true),
            (new("date", "Date", "clock"), false),
            (new("cputemp", "CPU temperature", "system"), true),
            (new("gputemp", "GPU temperature", "system"), true),
            (new("cpuload", "CPU load meter", "load"), false),
            (new("gpuload", "GPU load meter", "load"), false),
            (new("ram", "RAM meter", "load"), false),
            (new("ping", "Ping", "net"), false),
            (new("throttle", "Throttling chip (only while throttling)", "tail"), true),
            (new("music", "Spotify track", "music"), true),
        },
        [Surface.WideGame] = new (Module, bool)[]
        {
            (new("icon", "Game icon", "id"), true),
            (new("name", "Game name", "id"), true),
            (new("fps", "FPS", "game"), true),
            (new("avg", "Average FPS", "game"), true),
            (new("low1", "1% low", "game"), true),
            (new("low01", "0.1% low", "game"), true),
            (new("frametime", "Frame time (ms)", "game"), true),
            (new("graph", "Frame-time graph", "game"), true),
            (new("score", "Smoothness ring", "game"), true),
            (new("stutter", "Stutters (when there are any)", "game"), true),
            (new("status", "Status chip (bottleneck, scene, recording)", "tail"), true),
        },
        [Surface.WideSystem] = new (Module, bool)[]
        {
            (new("throttle", "Throttling chip (only while throttling)", "tail"), true),
            (new("cpu", "CPU load and temperature", "cpu"), true),
            (new("cpudetail", "CPU clock and power", "cpu"), true),
            (new("gpu", "GPU load and temperature", "gpu"), true),
            (new("gpudetail", "GPU clock and power", "gpu"), true),
            (new("vram", "VRAM meter", "mem"), true),
            (new("ram", "RAM meter", "mem"), true),
            (new("ping", "Ping", "net"), true),
        },
    };

    public static Module? Find(Surface s, string id) => Catalog[s].Select(c => c.m).FirstOrDefault(m => m.Id == id);

    public static List<Slot> Default(Surface s) => Catalog[s].Select(c => new Slot(c.m.Id, c.on)).ToList();

    /// <summary>Drops unknown entries and duplicates, and adds modules that are new since the layout was saved.</summary>
    public static List<Slot> Normalize(List<Slot>? slots, Surface s)
    {
        if (slots == null || slots.Count == 0) return Default(s);
        var seen = new HashSet<string>();
        var list = slots.Where(x => x != null && Find(s, x.Id) != null && seen.Add(x.Id)).Select(x => new Slot(x.Id, x.On)).ToList();
        foreach (var (m, on) in Catalog[s])
            if (seen.Add(m.Id)) list.Add(new Slot(m.Id, on));
        return list;
    }
}

/// <summary>The chosen metrics and their order on every surface.</summary>
sealed class LayoutSet
{
    public List<Slot> Game { get; set; } = Modules.Default(Surface.Game);
    public List<Slot> Idle { get; set; } = Modules.Default(Surface.Idle);
    public List<Slot> WideGame { get; set; } = Modules.Default(Surface.WideGame);
    public List<Slot> WideSystem { get; set; } = Modules.Default(Surface.WideSystem);

    public List<Slot> Get(Surface s) => s switch
    {
        Surface.Game => Game,
        Surface.Idle => Idle,
        Surface.WideGame => WideGame,
        _ => WideSystem,
    };

    public void Set(Surface s, List<Slot> slots)
    {
        switch (s)
        {
            case Surface.Game: Game = slots; break;
            case Surface.Idle: Idle = slots; break;
            case Surface.WideGame: WideGame = slots; break;
            default: WideSystem = slots; break;
        }
    }

    public LayoutSet Normalized()
    {
        var copy = new LayoutSet();
        foreach (Surface s in Enum.GetValues<Surface>()) copy.Set(s, Modules.Normalize(Get(s), s));
        return copy;
    }

    public LayoutSet Clone() => Normalized();
}

/// <summary>A saved look: theme, placement and layouts. Settings edits go into the profile in use.</summary>
sealed class Profile
{
    public string Name { get; set; } = "Default";
    public string Accent { get; set; } = "Ice";
    public string Position { get; set; } = "Center";
    public double Scale { get; set; } = 1.0;
    public int Opacity { get; set; } = 94;
    public int OffsetX { get; set; }
    public bool Wide { get; set; }
    public LayoutSet Layout { get; set; } = new();

    public static Profile From(Config c, string name) => new()
    {
        Name = name, Accent = c.Accent, Position = c.Position, Scale = c.Scale, Opacity = c.Opacity,
        OffsetX = c.OffsetX, Wide = c.Wide, Layout = c.Layout.Clone(),
    };

    public void CaptureFrom(Config c)
    {
        Accent = c.Accent; Position = c.Position; Scale = c.Scale; Opacity = c.Opacity;
        OffsetX = c.OffsetX; Wide = c.Wide; Layout = c.Layout.Clone();
    }

    public void ApplyTo(Config c)
    {
        c.Accent = Accent; c.Position = Position; c.Scale = Scale; c.Opacity = Opacity;
        c.OffsetX = OffsetX; c.Wide = Wide; c.Layout = Layout.Clone();
    }

    public Profile Copy(string name)
    {
        var p = (Profile)MemberwiseClone();
        p.Name = name;
        p.Layout = Layout.Clone();
        return p;
    }
}

/// <summary>A game that switches to its own profile while it runs.</summary>
sealed class GameProfile
{
    public string Key { get; set; } = "";     // the session key (exe name, or "Minecraft")
    public string Game { get; set; } = "";    // display name
    public string Profile { get; set; } = "";
}
