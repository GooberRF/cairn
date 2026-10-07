using System.Text;
using Cairn.Formats.Vpp;

namespace Cairn.Vpp.Editing;

/// <summary>Entry-name rules of RF1 packfiles: Latin-1, at most 59 bytes plus the NUL, no folders, unique ignoring case.</summary>
public static class VppNames
{
    /// <summary>Longest storable name in bytes (the 60-byte field keeps one byte for the terminating NUL).</summary>
    public const int MaxNameBytes = VppArchive.NameBytes - 1;

    /// <summary>
    /// Name identity everywhere in the packfile module: the game looks names up ignoring case, but folds
    /// A-Z only (C <c>tolower</c>), so "Ü.tga" and "ü.tga" are different entries.
    /// </summary>
    public static StringComparer Comparer { get; } = new AsciiIgnoreCaseComparer();

    /// <summary>True when the name is plain ASCII.</summary>
    public static bool IsAscii(string name)
    {
        foreach (char c in name) if (c > '\u007F') return false;
        return true;
    }

    private sealed class AsciiIgnoreCaseComparer : StringComparer
    {
        private static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

        public override int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            int n = Math.Min(x.Length, y.Length);
            for (int i = 0; i < n; i++)
            {
                int d = Fold(x[i]) - Fold(y[i]);
                if (d != 0) return d;
            }
            return x.Length - y.Length;
        }

        public override bool Equals(string? x, string? y) => Compare(x, y) == 0;

        public override int GetHashCode(string obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            var hash = new HashCode();
            foreach (char c in obj) hash.Add(Fold(c));
            return hash.ToHashCode();
        }
    }

    /// <summary>The encoding names are stored in.</summary>
    public static Encoding Encoding => Encoding.Latin1;

    private static readonly char[] Separators = ['/', '\\', ':'];

    /// <summary>Trims surrounding white space, drops any folder part and composes accents (NFC).</summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string trimmed = name.Trim();
        int slash = trimmed.LastIndexOfAny(Separators);
        if (slash >= 0) trimmed = trimmed[(slash + 1)..].Trim();
        return trimmed.Normalize(NormalizationForm.FormC);
    }

    /// <summary>True when every character fits in one Latin-1 byte.</summary>
    public static bool IsLatin1(string name)
    {
        foreach (char c in name) if (c > 'ÿ') return false;
        return true;
    }

    /// <summary>Bytes the name occupies when stored (characters outside Latin-1 count once, as the '?' they would become).</summary>
    public static int ByteLength(string name) => name.Length;

    /// <summary>Why the name cannot be stored, or null when it can.</summary>
    public static string? Validate(string name)
    {
        if (string.IsNullOrEmpty(name)) return "The name is empty.";
        if (name.Trim().Length == 0) return "The name is only white space.";
        if (name.IndexOfAny(Separators) >= 0) return "Packfiles have no folders: the name must not contain '/', '\\' or ':'.";
        foreach (char c in name)
        {
            if (c < ' ' || c == '\u007F') return "The name contains a control character.";
        }
        if (!IsLatin1(name)) return "The name contains characters that cannot be stored (only Latin-1 is possible).";
        if (ByteLength(name) > MaxNameBytes) return $"The name is {ByteLength(name)} characters long; at most {MaxNameBytes} fit.";
        return null;
    }

    /// <summary>True when <see cref="Validate"/> finds nothing wrong.</summary>
    public static bool IsValid(string name) => Validate(name) is null;

    /// <summary>The lower-case extension including the dot, or "" when there is none.</summary>
    public static string ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? string.Empty : name[dot..].ToLowerInvariant();
    }

    /// <summary>
    /// Returns <paramref name="name"/> if it is free, else "stem (2).ext", "stem (3).ext", ... shortening
    /// the stem so the result stays within <see cref="MaxNameBytes"/>.
    /// </summary>
    public static string MakeUnique(string name, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(isTaken);
        if (!isTaken(name)) return name;
        int dot = name.LastIndexOf('.');
        string stem = dot > 0 ? name[..dot] : name;
        string ext = dot > 0 ? name[dot..] : string.Empty;
        if (ext.Length > MaxNameBytes / 2) { stem = name; ext = string.Empty; }
        for (int n = 2; ; n++)
        {
            string suffix = $" ({n})";
            int room = MaxNameBytes - suffix.Length - ext.Length;
            string s = stem.Length > room ? stem[..Math.Max(0, room)].TrimEnd() : stem;
            string candidate = s + suffix + ext;
            if (!isTaken(candidate)) return candidate;
        }
    }
}
