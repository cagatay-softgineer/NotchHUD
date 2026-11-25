using System.Drawing.Text;

namespace NotchHUD;

/// <summary>Outline of the notch body.</summary>
enum Shape
{
    Notch,  // flush with the screen edge, flared top corners, round bottom
    Pill,   // a floating capsule a few pixels below the edge
    Blade,  // flush top, chamfered bottom corners (one sharper than the other)
    Slab,   // flush top, nearly square corners
}

/// <summary>How the outline of the body is drawn.</summary>
enum Edge
{
    Gradient, // thin accent-to-accent2 line
    Neon,     // thick coloured tube with a bright core
    Shimmer,  // iridescent gradient that slowly travels around the edge (animated)
    Double,   // a line plus a second, inset line
    Dashed,   // segmented, like a terminal frame
    Brackets, // faint outline with bright corner brackets
    None,
}

/// <summary>Surface treatment painted inside the body.</summary>
enum Texture
{
    None,
    Glass,     // soft highlight across the top
    Scanlines, // CRT lines with a slow refresh band (animated)
    Grid,      // fine instrument grid
    Horizon,   // retro sun and a perspective grid that scrolls toward you (animated)
    Carbon,    // diagonal weave
    Hex,       // honeycomb
    Aurora,    // drifting colour curtains (animated)
    Stars,     // twinkling star field (animated)
    Embers,    // sparks rising from the bottom (animated)
    Grain,     // film grain
}

enum MeterStyle { Line, Segments, Capsule }
enum RingStyle { Arc, Ticks, Thin }
enum SparkStyle { Area, Bars, Step }
enum ChipStyle { Fill, Outline, Bracket, Tag }
enum SepStyle { Line, Dot, Slash }
enum DotStyle { Circle, Square, Diamond }

/// <summary>Everything that decides how the notch looks.</summary>
sealed class ThemeSpec
{
    public string Name { get; init; } = "";
    public string About { get; init; } = "";

    // Palette
    public Color Accent { get; init; } = Color.FromArgb(96, 210, 255);
    public Color Accent2 { get; init; } = Color.FromArgb(167, 139, 250);
    public Color Accent3 { get; init; } = Color.FromArgb(110, 231, 183); // third colour for aurora and shimmer
    public Color BodyTop { get; init; } = Color.FromArgb(17, 20, 27);
    public Color BodyBottom { get; init; } = Color.FromArgb(2, 3, 5);
    public Color Ink { get; init; } = Color.White;                      // main text and values
    public Color Label { get; init; } = Color.FromArgb(150, 152, 160);
    public Color Soft { get; init; } = Color.FromArgb(214, 216, 222);
    public Color Faint { get; init; } = Color.FromArgb(110, 112, 120);
    public Color Good { get; init; } = Color.FromArgb(120, 222, 150);
    public Color Warn { get; init; } = Color.FromArgb(255, 200, 92);
    public Color Hot { get; init; } = Color.FromArgb(255, 104, 96);
    public Color Sweep { get; init; } = Color.White;                    // light that runs along the bottom on a change
    public Color OnAccent { get; init; } = Color.Black;                 // text on a solid accent tag

    // Body
    public Shape Shape { get; init; } = Shape.Notch;
    public Edge Edge { get; init; } = Edge.Gradient;
    public Texture Texture { get; init; } = Texture.None;
    public float TextureStrength { get; init; } = 1f;
    public bool Sheen { get; init; }        // glass highlight on top of any texture
    public float Glow { get; init; } = 1f;  // resting glow strength
    public bool Shadow { get; init; }       // dark drop shadow, for light and floating themes
    public bool CenterMark { get; init; }   // small luminous bar centred on the bottom edge
    public float Round { get; init; } = 1f; // corner rounding of inner boxes, 0 = square

    // Parts
    public MeterStyle Meter { get; init; } = MeterStyle.Line;
    public RingStyle Ring { get; init; } = RingStyle.Arc;
    public SparkStyle Spark { get; init; } = SparkStyle.Area;
    public ChipStyle Chip { get; init; } = ChipStyle.Fill;
    public SepStyle Sep { get; init; } = SepStyle.Line;
    public DotStyle Dot { get; init; } = DotStyle.Circle;

    // Type
    public string NumRegular { get; init; } = "Bahnschrift";
    public string NumBold { get; init; } = "Bahnschrift SemiBold";
    public float NumScale { get; init; } = 1f;
    public string TitleFont { get; init; } = "Segoe UI Semibold";
    public string TextFont { get; init; } = "Segoe UI";

    /// <summary>True when the look changes over time, so the notch keeps redrawing gently.</summary>
    public bool Animated => Edge == Edge.Shimmer || Texture is Texture.Scanlines or Texture.Horizon
        or Texture.Aurora or Texture.Stars or Texture.Embers;

    public bool Light => Ink.GetBrightness() < 0.4f;
}

static class Theme
{
    static readonly ThemeSpec[] All =
    {
        // ---------------------------------------------------------------- classics, refreshed
        new()
        {
            Name = "Ice", About = "Cool cyan glass. The original look, with a soft top highlight.",
            Sheen = true,
        },
        new()
        {
            Name = "Mint", About = "Fresh mint and sky blue on glass.",
            Accent = Color.FromArgb(110, 231, 183), Accent2 = Color.FromArgb(96, 210, 255),
            BodyTop = Color.FromArgb(12, 24, 22), BodyBottom = Color.FromArgb(1, 5, 4), Sheen = true,
            Meter = MeterStyle.Capsule,
        },
        new()
        {
            Name = "Violet", About = "Violet fading into rose, glossy capsule meters.",
            Accent = Color.FromArgb(167, 139, 250), Accent2 = Color.FromArgb(251, 113, 133),
            BodyTop = Color.FromArgb(22, 16, 34), BodyBottom = Color.FromArgb(4, 3, 8), Sheen = true,
            Meter = MeterStyle.Capsule, Ring = RingStyle.Thin,
        },
        new()
        {
            Name = "Amber", About = "Warm amber, like a cockpit at night.",
            Accent = Color.FromArgb(251, 191, 36), Accent2 = Color.FromArgb(251, 113, 133),
            BodyTop = Color.FromArgb(26, 19, 10), BodyBottom = Color.FromArgb(5, 3, 1), Sheen = true,
        },
        new()
        {
            Name = "Rose", About = "Rose and lavender.",
            Accent = Color.FromArgb(251, 113, 133), Accent2 = Color.FromArgb(167, 139, 250),
            BodyTop = Color.FromArgb(30, 14, 20), BodyBottom = Color.FromArgb(6, 2, 4), Sheen = true,
            Meter = MeterStyle.Capsule,
        },
        new()
        {
            Name = "White", About = "Monochrome white with an ice-blue edge.",
            Accent = Color.FromArgb(235, 235, 240), Accent2 = Color.FromArgb(96, 210, 255), Sheen = true,
            Chip = ChipStyle.Outline, Ring = RingStyle.Thin,
        },
        new()
        {
            Name = "Synthwave", About = "Neon tube edge, retro sun and a grid rolling toward you.",
            Accent = Color.FromArgb(255, 96, 208), Accent2 = Color.FromArgb(96, 210, 255),
            Accent3 = Color.FromArgb(255, 170, 60),
            BodyTop = Color.FromArgb(40, 14, 50), BodyBottom = Color.FromArgb(9, 3, 16),
            Edge = Edge.Neon, Texture = Texture.Horizon, Glow = 2.2f,
            Spark = SparkStyle.Area, Chip = ChipStyle.Outline, Sep = SepStyle.Slash,
            Label = Color.FromArgb(196, 150, 210), Soft = Color.FromArgb(240, 214, 240),
        },
        new()
        {
            Name = "Matrix", About = "Green phosphor terminal: monospace, scanlines, stepped graph.",
            Accent = Color.FromArgb(74, 246, 128), Accent2 = Color.FromArgb(20, 170, 80),
            BodyTop = Color.FromArgb(5, 24, 11), BodyBottom = Color.FromArgb(0, 4, 1),
            Shape = Shape.Slab, Edge = Edge.Dashed, Texture = Texture.Scanlines, Glow = 1.6f, Round = 0.25f,
            Meter = MeterStyle.Segments, Ring = RingStyle.Ticks, Spark = SparkStyle.Step,
            Chip = ChipStyle.Bracket, Sep = SepStyle.Slash, Dot = DotStyle.Square,
            NumRegular = "Cascadia Mono", NumBold = "Cascadia Mono SemiBold", NumScale = 0.88f,
            TitleFont = "Cascadia Mono SemiBold", TextFont = "Cascadia Mono",
            Ink = Color.FromArgb(200, 255, 210), Label = Color.FromArgb(70, 160, 95),
            Soft = Color.FromArgb(150, 230, 170), Faint = Color.FromArgb(40, 110, 60),
            Good = Color.FromArgb(74, 246, 128), Sweep = Color.FromArgb(74, 246, 128),
        },
        new()
        {
            Name = "Stealth", About = "No glow, no colour, square edges. Stays out of the way.",
            Accent = Color.FromArgb(204, 208, 216), Accent2 = Color.FromArgb(120, 124, 132),
            BodyTop = Color.FromArgb(18, 18, 20), BodyBottom = Color.FromArgb(4, 4, 5),
            Shape = Shape.Slab, Edge = Edge.None, Glow = 0f, Round = 0.4f,
            Ring = RingStyle.Thin, Chip = ChipStyle.Outline, Sep = SepStyle.Dot,
        },

        // ---------------------------------------------------------------- advanced
        new()
        {
            Name = "Cyberpunk", About = "Hazard yellow blade with chamfered corners, solid tags and segmented meters.",
            Accent = Color.FromArgb(252, 238, 10), Accent2 = Color.FromArgb(0, 240, 255),
            BodyTop = Color.FromArgb(20, 18, 6), BodyBottom = Color.FromArgb(3, 3, 0),
            Shape = Shape.Blade, Edge = Edge.Brackets, Texture = Texture.Carbon, Glow = 1.3f, Round = 0f,
            CenterMark = true,
            Meter = MeterStyle.Segments, Ring = RingStyle.Ticks, Spark = SparkStyle.Bars,
            Chip = ChipStyle.Tag, Sep = SepStyle.Slash, Dot = DotStyle.Diamond,
            NumRegular = "Bahnschrift Condensed", NumBold = "Bahnschrift SemiBold Condensed", NumScale = 1.08f,
            Label = Color.FromArgb(0, 200, 215), Soft = Color.FromArgb(230, 230, 200),
            Hot = Color.FromArgb(255, 60, 90), Sweep = Color.FromArgb(0, 240, 255),
        },
        new()
        {
            Name = "Aurora", About = "A floating glass capsule with northern lights drifting behind the numbers.",
            Accent = Color.FromArgb(90, 255, 190), Accent2 = Color.FromArgb(150, 110, 255),
            Accent3 = Color.FromArgb(60, 190, 255),
            BodyTop = Color.FromArgb(10, 18, 28), BodyBottom = Color.FromArgb(2, 4, 10),
            Shape = Shape.Pill, Texture = Texture.Aurora, Sheen = true, Shadow = true, Glow = 0.8f,
            Meter = MeterStyle.Capsule, Ring = RingStyle.Thin, Sep = SepStyle.Dot,
            NumRegular = "Bahnschrift SemiLight", NumBold = "Bahnschrift SemiBold",
            Label = Color.FromArgb(140, 170, 180), Soft = Color.FromArgb(210, 235, 235),
        },
        new()
        {
            Name = "Holo", About = "Iridescent edge that shifts colour as it travels around the notch.",
            Accent = Color.FromArgb(255, 120, 220), Accent2 = Color.FromArgb(90, 230, 255),
            Accent3 = Color.FromArgb(255, 230, 120),
            BodyTop = Color.FromArgb(18, 15, 30), BodyBottom = Color.FromArgb(4, 3, 10),
            Edge = Edge.Shimmer, Texture = Texture.Grain, Sheen = true, Glow = 1.4f,
            Chip = ChipStyle.Outline, Ring = RingStyle.Thin, Meter = MeterStyle.Capsule,
            Label = Color.FromArgb(170, 160, 200), Soft = Color.FromArgb(230, 225, 245),
        },
        new()
        {
            Name = "Phosphor", About = "Amber CRT terminal: scanlines, a rolling refresh band, block meters.",
            Accent = Color.FromArgb(255, 176, 0), Accent2 = Color.FromArgb(255, 120, 0),
            BodyTop = Color.FromArgb(22, 13, 0), BodyBottom = Color.FromArgb(4, 2, 0),
            Shape = Shape.Slab, Edge = Edge.Dashed, Texture = Texture.Scanlines, Glow = 1.5f, Round = 0f,
            TextureStrength = 1.3f,
            Meter = MeterStyle.Segments, Ring = RingStyle.Ticks, Spark = SparkStyle.Step,
            Chip = ChipStyle.Bracket, Sep = SepStyle.Slash, Dot = DotStyle.Square,
            NumRegular = "Cascadia Mono", NumBold = "Cascadia Mono SemiBold", NumScale = 0.88f,
            TitleFont = "Cascadia Mono SemiBold", TextFont = "Cascadia Mono",
            Ink = Color.FromArgb(255, 214, 140), Label = Color.FromArgb(170, 110, 20),
            Soft = Color.FromArgb(240, 180, 90), Faint = Color.FromArgb(120, 76, 10),
            Good = Color.FromArgb(200, 240, 90), Warn = Color.FromArgb(255, 210, 60), Hot = Color.FromArgb(255, 90, 40),
            Sweep = Color.FromArgb(255, 176, 0),
        },
        new()
        {
            Name = "Tactical", About = "Military HUD: olive grid, corner brackets, tick gauges.",
            Accent = Color.FromArgb(180, 230, 80), Accent2 = Color.FromArgb(120, 170, 60),
            BodyTop = Color.FromArgb(14, 18, 10), BodyBottom = Color.FromArgb(3, 4, 2),
            Shape = Shape.Slab, Edge = Edge.Brackets, Texture = Texture.Grid, Glow = 0.9f, Round = 0.15f,
            CenterMark = true,
            Meter = MeterStyle.Segments, Ring = RingStyle.Ticks, Spark = SparkStyle.Bars,
            Chip = ChipStyle.Bracket, Sep = SepStyle.Line, Dot = DotStyle.Diamond,
            NumRegular = "Bahnschrift Condensed", NumBold = "Bahnschrift SemiBold Condensed", NumScale = 1.08f,
            Label = Color.FromArgb(130, 150, 100), Soft = Color.FromArgb(210, 225, 190),
            Sweep = Color.FromArgb(180, 230, 80),
        },
        new()
        {
            Name = "Glass", About = "Frosted glass capsule with grain and a crisp white rim.",
            Accent = Color.FromArgb(230, 240, 255), Accent2 = Color.FromArgb(150, 200, 255),
            BodyTop = Color.FromArgb(58, 62, 74), BodyBottom = Color.FromArgb(20, 22, 28),
            Shape = Shape.Pill, Texture = Texture.Grain, TextureStrength = 1.4f, Sheen = true, Shadow = true,
            Glow = 0.5f, Meter = MeterStyle.Capsule, Ring = RingStyle.Thin, Sep = SepStyle.Dot,
            Label = Color.FromArgb(175, 182, 196), Soft = Color.FromArgb(225, 230, 240),
        },
        new()
        {
            Name = "Daylight", About = "A light capsule with dark text, for bright desktops.",
            Accent = Color.FromArgb(0, 113, 227), Accent2 = Color.FromArgb(120, 80, 230),
            BodyTop = Color.FromArgb(252, 252, 254), BodyBottom = Color.FromArgb(230, 232, 238),
            Ink = Color.FromArgb(18, 20, 26), Label = Color.FromArgb(108, 112, 124),
            Soft = Color.FromArgb(58, 62, 72), Faint = Color.FromArgb(150, 154, 164),
            Good = Color.FromArgb(22, 163, 74), Warn = Color.FromArgb(217, 119, 6), Hot = Color.FromArgb(220, 38, 38),
            Sweep = Color.FromArgb(0, 113, 227), OnAccent = Color.White,
            Shape = Shape.Pill, Edge = Edge.None, Shadow = true, Glow = 0f,
            Meter = MeterStyle.Capsule, Ring = RingStyle.Arc, Chip = ChipStyle.Fill, Sep = SepStyle.Dot,
        },
        new()
        {
            Name = "Nebula", About = "Deep space purple with a twinkling star field.",
            Accent = Color.FromArgb(190, 120, 255), Accent2 = Color.FromArgb(80, 200, 255),
            Accent3 = Color.FromArgb(255, 140, 200),
            BodyTop = Color.FromArgb(20, 10, 36), BodyBottom = Color.FromArgb(3, 2, 8),
            Texture = Texture.Stars, Sheen = true, Glow = 1.5f,
            Ring = RingStyle.Thin, Meter = MeterStyle.Capsule, Sep = SepStyle.Dot,
            Label = Color.FromArgb(160, 140, 200), Soft = Color.FromArgb(225, 215, 245),
        },
        new()
        {
            Name = "Inferno", About = "Molten orange with embers rising off the bottom edge.",
            Accent = Color.FromArgb(255, 130, 30), Accent2 = Color.FromArgb(255, 40, 70),
            Accent3 = Color.FromArgb(255, 210, 90),
            BodyTop = Color.FromArgb(10, 3, 1), BodyBottom = Color.FromArgb(44, 10, 2),
            Edge = Edge.Neon, Texture = Texture.Embers, Glow = 1.8f,
            Spark = SparkStyle.Bars, Ring = RingStyle.Arc, Meter = MeterStyle.Segments, Chip = ChipStyle.Outline,
            Label = Color.FromArgb(200, 130, 100), Soft = Color.FromArgb(250, 220, 200),
            Warn = Color.FromArgb(255, 220, 90), Hot = Color.FromArgb(255, 70, 90),
        },
        new()
        {
            Name = "Luxe", About = "Black and gold: double gilt edge, serif titles, honeycomb weave.",
            Accent = Color.FromArgb(232, 190, 110), Accent2 = Color.FromArgb(255, 232, 175),
            BodyTop = Color.FromArgb(20, 17, 12), BodyBottom = Color.FromArgb(3, 3, 2),
            Edge = Edge.Double, Texture = Texture.Hex, TextureStrength = 0.8f, Glow = 0.7f, CenterMark = true,
            Ring = RingStyle.Thin, Meter = MeterStyle.Line, Chip = ChipStyle.Outline, Dot = DotStyle.Diamond,
            NumRegular = "Bahnschrift Light", NumBold = "Bahnschrift SemiBold",
            TitleFont = "Georgia", TextFont = "Segoe UI",
            Label = Color.FromArgb(170, 150, 115), Soft = Color.FromArgb(235, 225, 200),
            Ink = Color.FromArgb(255, 248, 232),
        },
        new()
        {
            Name = "Dracula", About = "The popular dark editor palette: purple, pink and green.",
            Accent = Color.FromArgb(189, 147, 249), Accent2 = Color.FromArgb(255, 121, 198),
            BodyTop = Color.FromArgb(44, 46, 60), BodyBottom = Color.FromArgb(24, 25, 33),
            Ink = Color.FromArgb(248, 248, 242), Label = Color.FromArgb(130, 140, 180),
            Soft = Color.FromArgb(220, 220, 230), Faint = Color.FromArgb(98, 114, 164),
            Good = Color.FromArgb(80, 250, 123), Warn = Color.FromArgb(241, 250, 140), Hot = Color.FromArgb(255, 85, 85),
            Sheen = true, Meter = MeterStyle.Capsule, Chip = ChipStyle.Fill,
        },
        new()
        {
            Name = "Nord", About = "Calm arctic blues and frost.",
            Accent = Color.FromArgb(136, 192, 208), Accent2 = Color.FromArgb(129, 161, 193),
            BodyTop = Color.FromArgb(59, 66, 82), BodyBottom = Color.FromArgb(36, 41, 51),
            Ink = Color.FromArgb(236, 239, 244), Label = Color.FromArgb(150, 160, 178),
            Soft = Color.FromArgb(216, 222, 233), Faint = Color.FromArgb(110, 120, 138),
            Good = Color.FromArgb(163, 190, 140), Warn = Color.FromArgb(235, 203, 139), Hot = Color.FromArgb(191, 97, 106),
            Shape = Shape.Pill, Sheen = true, Shadow = true, Glow = 0.4f,
            Meter = MeterStyle.Capsule, Ring = RingStyle.Thin, Sep = SepStyle.Dot,
        },
    };

    static readonly Dictionary<string, ThemeSpec> ByName = All.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The theme in use. Set from the config with <see cref="Use"/>.</summary>
    public static ThemeSpec Current { get; private set; } = All[0];

    public static void Use(string name) => Current = Get(name);

    public static ThemeSpec Get(string name) => ByName.TryGetValue(name ?? "", out var t) ? t : All[0];

    public static string[] AccentNames { get; } = All.Select(t => t.Name).ToArray();

    public static Color Label => Current.Label;
    public static Color Soft => Current.Soft;
    public static Color Good => Current.Good;
    public static Color Warn => Current.Warn;
    public static Color Hot => Current.Hot;
    public static Color Ink => Current.Ink;
    public static Color Faint => Current.Faint;

    /// <summary>The ink colour at a given alpha, for tracks, grids and dividers.</summary>
    public static Color Line(int alpha) => Color.FromArgb(Current.Light ? Math.Min(255, alpha * 3 / 2) : alpha, Current.Ink);

    public static Color Accent(string name) => Get(name).Accent;

    static HashSet<string>? _installed;

    /// <summary>The font if it is installed, otherwise the fallback.</summary>
    public static string Font(string name, string fallback)
    {
        _installed ??= new HashSet<string>(new InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        return _installed.Contains(name) ? name : fallback;
    }
}
