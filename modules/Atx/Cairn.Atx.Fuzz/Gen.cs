using System.Globalization;
using System.Text;

namespace Cairn.Atx.Fuzz;

/// <summary>A frame as the generator intended it (the "simple list representation").</summary>
public sealed class GFrame
{
    public int Id;                     // unique marker id
    public string? File;               // null = no file key; "" = empty string value
    public long? FrameTime;            // raw 64-bit value written
    public string? Material;
    public List<int> AttachedComments = new();   // marker ids of comments attached above [[frame]]
}

public sealed class GDoc
{
    public string Text = "";
    public bool HasBom;
    public string Eol = "\r\n";
    public bool UniformEol = true;
    public bool EndsWithBreak = true;
    public bool Canonical = true;
    public bool HeaderPresent;
    public bool HeaderIsRealTable;
    public long? FrameTime;
    public bool? InitiallyOn;
    public long? AnimationMode;
    public string? Format;
    public string? AlphaMask;
    public string? Material;
    public List<GFrame> Frames = new();
    public List<string> Markers = new();   // every unique marker token present in the text
    public string Seedinfo = "";
}

public sealed class Rng
{
    private readonly Random _r;
    public Rng(int seed) { _r = new Random(seed); }
    public int Next(int n) => _r.Next(n);
    public int Next(int lo, int hi) => _r.Next(lo, hi);
    public bool Chance(int percent) => _r.Next(100) < percent;
    public double NextDouble() => _r.NextDouble();
    public T Pick<T>(IReadOnlyList<T> items) => items[_r.Next(items.Count)];
    public long NextLong()
    {
        Span<byte> b = stackalloc byte[8];
        _r.NextBytes(b);
        return BitConverter.ToInt64(b);
    }
}

/// <summary>Independent TOML writers so the generator does not depend on the code under test.</summary>
public static class Toml
{
    public static string Basic(string v)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in v)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < 0x20 || c == 0x7F) sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    public static bool LiteralSafe(string v)
    {
        foreach (char c in v)
        {
            if (c == '\'' || c == '\n' || c == '\r') return false;
            if (c < 0x20 && c != '\t') return false;
            if (c == 0x7F) return false;
        }
        return true;
    }

    public static string Literal(string v) => "'" + v + "'";

    /// <summary>Multi-line basic string whose first line break is the trimmed one (value has no newline).</summary>
    public static string MultilineBasic(string v, string eol)
    {
        var inner = Basic(v);
        return "\"\"\"" + eol + inner[1..^1] + "\"\"\"";
    }

    public static string MultilineLiteral(string v, string eol) => "'''" + eol + v + "'''";

    public static string Key(string name, Rng r) => r.Next(3) switch
    {
        0 => name,
        1 => "\"" + name + "\"",
        _ => "'" + name + "'",
    };

    public static string Int(long v, Rng r)
    {
        int style = r.Next(6);
        if (v < 0) style = r.Next(2);      // hex/oct/bin are non-negative in TOML
        return style switch
        {
            0 => v.ToString(CultureInfo.InvariantCulture),
            1 => v >= 0 ? "+" + v.ToString(CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture),
            2 => Underscored(v),
            3 => "0x" + ((ulong)v).ToString("x", CultureInfo.InvariantCulture),
            4 => "0o" + Convert.ToString(v, 8),
            _ => "0b" + Convert.ToString(v, 2),
        };
    }

    private static string Underscored(long v)
    {
        if (v == long.MinValue) return v.ToString(CultureInfo.InvariantCulture);
        string s = Math.Abs(v).ToString(CultureInfo.InvariantCulture);
        if (s.Length < 2) return v.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        if (v < 0) sb.Append('-');
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && i < s.Length) sb.Append('_');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public static string Str(string v, Rng r, string eol)
    {
        int style = Generator.NoMultiline ? r.Next(2) : r.Next(5);
        if ((style == 1 || style == 4) && !LiteralSafe(v)) style = 0;
        return style switch
        {
            0 => Basic(v),
            1 => Literal(v),
            2 => MultilineBasic(v, eol),
            3 => Basic(v),
            _ => MultilineLiteral(v, eol),
        };
    }
}

public static class Generator
{
    /// <summary>
    /// When true the generator emits no BOM and no multi-line strings, so that failures caused by
    /// the two known root causes stop masking anything else.
    /// </summary>
    public static bool NoMultiline;

    private static readonly string[] Hostile =
    {
        "plain", "with space", "with\"quote", "back\\slash", "hash#mark", "tab\there",
        "emoji\U0001F600x", "unié中", "trail ", " lead", "ctrlx", "",
        "dot.in.name", "UPPER", "semi;colon", "bracket[]", "brace{}", "eq=sign",
        "nl\nline", "cr\rline",
    };

    private static readonly string[] Exts = { ".tga", ".dds", ".png", ".jpg", ".vbm", ".ATX", "", ".bmp" };

    private static readonly string[] Materials =
        { "metal", "rock", "METAL", "wattr", "", "glass", "unknownmat", "wa ter" };

    private static readonly string[] Formats = { "565", "8888_argb", "4444", "", "nope", "888_RGB" };

    public static GDoc Generate(Rng r, bool canonicalOnly, int seed)
    {
        var d = new GDoc { Seedinfo = seed.ToString(CultureInfo.InvariantCulture) };
        d.Eol = r.Next(20) switch { < 8 => "\n", 8 => "\r", _ => "\r\n" };
        d.UniformEol = true;
        d.HasBom = !canonicalOnly && !NoMultiline && r.Chance(8);
        bool mixed = !canonicalOnly && r.Chance(8);

        int markerId = 0;
        string NewMarker(char kind)
        {
            string m = $"{kind}{markerId++}";
            d.Markers.Add(m);
            return m;
        }

        var lines = new List<string>();
        void Add(string s) => lines.Add(s);

        // Preamble: comments / blank lines.
        int pre = r.Next(4);
        for (int i = 0; i < pre; i++)
        {
            if (r.Chance(30)) Add("");
            else Add("# preamble " + NewMarker('P') + (r.Chance(20) ? " [[frame]] file = \"x\"" : ""));
        }
        if (pre > 0 && r.Chance(60)) Add("");

        // Decide layout.
        d.HeaderPresent = r.Chance(75);
        int frameCount = r.Next(101) switch
        {
            < 8 => 0,
            < 25 => 1,
            < 90 => r.Next(2, 6),
            _ => r.Next(6, 12),
        };

        // Header style.
        int headerStyle = 0; // 0 = [header] table
        if (!canonicalOnly && d.HeaderPresent && r.Chance(20)) headerStyle = r.Next(1, 3); // 1 inline, 2 dotted

        // A top-level `header = {...}` or `header.x = ...` after a [[frame]] would belong to that
        // frame's table, not the document root, so those styles must come first.
        bool headerFirst = headerStyle != 0 || r.Chance(75);
        int headerAfterFrame = headerFirst ? -1 : (frameCount == 0 ? 0 : r.Next(frameCount + 1));
        d.HeaderIsRealTable = headerStyle == 0;
        if (headerStyle != 0) d.Canonical = false;

        // Header values.
        if (d.HeaderPresent)
        {
            if (r.Chance(70)) d.FrameTime = PickFrameTime(r);
            if (r.Chance(45)) d.InitiallyOn = r.Chance(50);
            if (r.Chance(55)) d.AnimationMode = r.Next(101) < 80 ? r.Next(0, 4) : r.NextLong() % 1000;
            if (r.Chance(35)) d.Format = r.Pick(Formats);
            if (r.Chance(25)) d.AlphaMask = "mask" + r.Next(5) + ".tga";
            if (r.Chance(35)) d.Material = r.Pick(Materials);
        }

        for (int k = 0; k < frameCount; k++)
        {
            var f = new GFrame { Id = k };
            f.File = r.Next(100) < 90
                ? $"f{k}_" + r.Pick(Hostile) + r.Pick(Exts)
                : (r.Chance(50) ? "" : null);
            if (r.Chance(45)) f.FrameTime = PickFrameTime(r);
            if (r.Chance(30)) f.Material = r.Pick(Materials);
            d.Frames.Add(f);
        }

        void EmitHeader()
        {
            // Comments attached to the header block.
            if (r.Chance(35)) Add("# hdr " + NewMarker('H'));
            switch (headerStyle)
            {
                case 0:
                {
                    string decl = r.Next(4) switch
                    {
                        0 => "[header]",
                        1 => "[ header ]",
                        2 => "[header]  # " + NewMarker('X'),
                        _ => "  [header]",
                    };
                    Add(decl);
                    var kvs = new List<string>();
                    if (d.FrameTime is { } ft) kvs.Add(Assign("frame_time", Toml.Int(ft, r), r));
                    if (d.InitiallyOn is { } io) kvs.Add(Assign("initially_on", io ? "true" : "false", r));
                    if (d.AnimationMode is { } am) kvs.Add(Assign("animation_mode", Toml.Int(am, r), r));
                    if (d.Format is { } fm) kvs.Add(Assign("format", Toml.Str(fm, r, d.Eol), r));
                    if (d.AlphaMask is { } al) kvs.Add(Assign("alpha_mask", Toml.Str(al, r, d.Eol), r));
                    if (d.Material is { } mt) kvs.Add(Assign("material", Toml.Str(mt, r, d.Eol), r));
                    // Shuffle key order.
                    for (int i = kvs.Count - 1; i > 0; i--) { int j = r.Next(i + 1); (kvs[i], kvs[j]) = (kvs[j], kvs[i]); }
                    foreach (string kv in kvs)
                    {
                        if (r.Chance(18)) Add("# key note " + NewMarker('K'));
                        Add(kv + (r.Chance(18) ? "  # t" + NewMarker('T') : ""));
                    }
                    if (r.Chance(20)) Add("zz_unknown" + r.Next(3) + " = " + Toml.Basic("v" + NewMarker('U')));
                    break;
                }
                case 1:
                {
                    var parts = new List<string>();
                    if (d.FrameTime is { } ft) parts.Add("frame_time = " + Toml.Int(ft, r));
                    if (d.InitiallyOn is { } io) parts.Add("initially_on = " + (io ? "true" : "false"));
                    if (d.AnimationMode is { } am) parts.Add("animation_mode = " + Toml.Int(am, r));
                    if (d.Format is { } fm) parts.Add("format = " + Toml.Basic(fm));
                    if (d.AlphaMask is { } al) parts.Add("alpha_mask = " + Toml.Basic(al));
                    if (d.Material is { } mt) parts.Add("material = " + Toml.Basic(mt));
                    Add("header = { " + string.Join(", ", parts) + " }");
                    break;
                }
                default:
                {
                    if (d.FrameTime is { } ft) Add("header.frame_time = " + Toml.Int(ft, r));
                    if (d.InitiallyOn is { } io) Add("header.initially_on = " + (io ? "true" : "false"));
                    if (d.AnimationMode is { } am) Add("header.animation_mode = " + Toml.Int(am, r));
                    if (d.Format is { } fm) Add("header.format = " + Toml.Basic(fm));
                    if (d.AlphaMask is { } al) Add("header.alpha_mask = " + Toml.Basic(al));
                    if (d.Material is { } mt) Add("header.material = " + Toml.Basic(mt));
                    if (d.FrameTime is null && d.InitiallyOn is null && d.AnimationMode is null
                        && d.Format is null && d.AlphaMask is null && d.Material is null)
                    {
                        Add("header.zz = 1");
                    }
                    break;
                }
            }
        }

        void EmitFrame(GFrame f)
        {
            int attached = r.Next(100) < 35 ? r.Next(1, 3) : 0;
            for (int i = 0; i < attached; i++)
            {
                string m = NewMarker('A');
                f.AttachedComments.Add(int.Parse(m[1..], CultureInfo.InvariantCulture));
                Add("# attached " + m + $" owner={f.Id}");
            }
            string decl = r.Next(5) switch
            {
                0 => "[[frame]]",
                1 => "[[ frame ]]",
                2 => "[[frame]] # " + NewMarker('X'),
                3 => "  [[frame]]  ",
                _ => "[[frame]]",
            };
            Add(decl);
            var kvs = new List<string>();
            if (f.File is { } file) kvs.Add(Assign("file", Toml.Str(file, r, d.Eol), r));
            if (f.FrameTime is { } ft) kvs.Add(Assign("frame_time", Toml.Int(ft, r), r));
            if (f.Material is { } mt) kvs.Add(Assign("material", Toml.Str(mt, r, d.Eol), r));
            for (int i = kvs.Count - 1; i > 0; i--) { int j = r.Next(i + 1); (kvs[i], kvs[j]) = (kvs[j], kvs[i]); }
            foreach (string kv in kvs)
            {
                if (r.Chance(12)) Add("# fk " + NewMarker('K') + $" owner={f.Id}");
                Add(kv + (r.Chance(15) ? "  # t" + NewMarker('T') : ""));
            }
            if (r.Chance(12) && !NoMultiline)
            {
                Add("note" + f.Id + " = \"\"\"" + d.Eol + "[[frame]]" + d.Eol + "file = \"trap"
                    + NewMarker('M') + ".tga\"" + d.Eol + "\"\"\"");
            }
            if (r.Chance(8) && !NoMultiline)
            {
                // A multi-line string whose LAST line looks like a comment.
                Add("note2_" + f.Id + " = \"\"\"" + d.Eol + "x" + d.Eol + "# fakecomment "
                    + NewMarker('M') + "\"\"\"");
            }
            if (r.Chance(10)) Add("zz" + f.Id + " = " + Toml.Basic("u" + NewMarker('U')));
        }

        if (headerFirst && d.HeaderPresent)
        {
            EmitHeader();
            if (r.Chance(85)) Add("");
        }

        for (int k = 0; k < d.Frames.Count; k++)
        {
            if (!headerFirst && d.HeaderPresent && k == headerAfterFrame)
            {
                EmitHeader();
                if (r.Chance(85)) Add("");
            }
            EmitFrame(d.Frames[k]);
            if (r.Chance(15)) Add("# detached " + NewMarker('D'));
            if (k + 1 < d.Frames.Count && r.Chance(85)) Add("");
        }
        if (!headerFirst && d.HeaderPresent && headerAfterFrame >= d.Frames.Count)
        {
            if (r.Chance(85)) Add("");
            EmitHeader();
        }

        if (!canonicalOnly && r.Chance(10))
        {
            Add("");
            Add("[misc_" + r.Next(1000) + "]");
            Add("q = " + Toml.Basic("z" + NewMarker('U')));
        }

        // Join.
        var sb = new StringBuilder();
        if (d.HasBom) sb.Append('﻿');
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i]);
            bool last = i == lines.Count - 1;
            string eol = d.Eol;
            if (mixed && r.Chance(30)) { eol = r.Next(3) switch { 0 => "\n", 1 => "\r", _ => "\r\n" }; d.UniformEol = false; }
            if (!last || r.Chance(80)) sb.Append(eol);
            else d.EndsWithBreak = false;
        }
        if (lines.Count == 0) d.EndsWithBreak = false;
        d.Text = sb.ToString();
        return d;
    }

    private static string Assign(string key, string value, Rng r)
    {
        string k = Toml.Key(key, r);
        return r.Next(4) switch
        {
            0 => k + " = " + value,
            1 => k + "=" + value,
            2 => k + "   =   " + value,
            _ => "  " + k + " = " + value,
        };
    }

    private static long PickFrameTime(Rng r) => r.Next(100) switch
    {
        < 55 => r.Next(1, 500),
        < 65 => 0,
        < 72 => -r.Next(1, 100),
        < 80 => 100,
        < 88 => r.Next(int.MaxValue - 5, int.MaxValue),
        < 94 => 4294967296L + r.Next(5),
        _ => r.NextLong(),
    };
}
