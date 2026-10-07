using System.Buffers.Binary;
using System.Text;

namespace Cairn.Formats.Rfl;

/// <summary>
/// Reads the summary of a Red Faction level (.rfl): identity, level properties, statistics and the
/// files it names. The reader walks the section stream (the header's offsets and totals are not
/// trusted), skips every section by its stated length, and reads each body inside its own bounds,
/// so a damaged or unknown section costs only that section. Only a file that is not a level at all
/// or whose header is cut short throws <see cref="AssetFormatException"/>; everything else is
/// reported in <see cref="RflSummary.Notes"/> and the section list.
/// </summary>
public static class RflReader
{
    /// <summary>The level signature (bytes 55 DA BA D4).</summary>
    public const uint Magic = 0xD4BADA55;

    /// <summary>The newest Alpine Faction level version this reader knows by name.</summary>
    public const int NewestKnownVersion = 306;

    // The longest possible header: fixed fields plus two strings of at most 65,535 bytes.
    private const int MaxHeaderBytes = 4 * 7 + 2 * (2 + ushort.MaxValue);

    /// <summary>Reads a level held in memory.</summary>
    /// <exception cref="AssetFormatException">The bytes are not a level, or its header is cut short.</exception>
    public static RflSummary ReadSummary(ReadOnlySpan<byte> data, string name)
    {
        var header = ReadHeader(data, name, data.Length);
        var walker = new Walker(name, header, data.Length);
        if (walker.WalksSections)
        {
            long pos = header.End;
            while (true)
            {
                long left = data.Length - pos;
                if (!walker.BeforeSection(pos, left)) break;
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)pos..]);
                int length = BinaryPrimitives.ReadInt32LittleEndian(data[(int)(pos + 4)..]);
                if (!walker.SectionHeader(id, length, pos, left - 8)) break;
                walker.Section(id, pos, length, data.Slice((int)pos + 8, length));
                pos += 8L + length;
            }
        }
        return walker.Finish();
    }

    /// <summary>
    /// Reads a level from a stream, from its current position to its end. On a seekable stream only
    /// the sections the summary needs are read (lightmaps and editor brushes are skipped by seeking);
    /// nothing past the stream's length is ever requested.
    /// </summary>
    /// <exception cref="AssetFormatException">The bytes are not a level, or its header is cut short.</exception>
    public static RflSummary ReadSummary(Stream stream, string name) => ReadStream(stream, name, identityOnly: false);

    /// <summary>
    /// Reads only a level's identity from a stream: the header (version, save time, header name) and the level info
    /// section (name, author, editor's date). Every other section is stepped over by its length without being read,
    /// so this costs a few small reads however large the level is. The rest of the summary is left empty.
    /// </summary>
    /// <exception cref="AssetFormatException">The bytes are not a level, or its header is cut short.</exception>
    public static RflSummary ReadIdentity(Stream stream, string name) => ReadStream(stream, name, identityOnly: true);

    private const uint LevelInfoSection = 0x01000000;

    private static RflSummary ReadStream(Stream stream, string name, bool identityOnly)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return ReadSummary(copy.GetBuffer().AsSpan(0, (int)copy.Length), name);
        }

        long start = stream.Position;
        long total = Math.Max(0, stream.Length - start);
        var buffer = new byte[(int)Math.Min(total, MaxHeaderBytes)];
        ReadExactly(stream, buffer, buffer.Length, name);
        var header = ReadHeader(buffer, name, total);
        var walker = new Walker(name, header, total);
        if (walker.WalksSections)
        {
            long pos = header.End;
            Span<byte> sectionHeader = stackalloc byte[8];
            while (true)
            {
                long left = total - pos;
                if (!walker.BeforeSection(pos, left)) break;
                stream.Position = start + pos;
                ReadExactly(stream, sectionHeader, 8, name);
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(sectionHeader);
                int length = BinaryPrimitives.ReadInt32LittleEndian(sectionHeader[4..]);
                if (!walker.SectionHeader(id, length, pos, left - 8)) break;
                if (identityOnly && id != LevelInfoSection)
                {
                    pos += 8L + length;
                    continue;
                }
                // The length was just checked against what the stream holds, so it bounds the buffer.
                int want = walker.BodyBytes(id, length);
                if (buffer.Length < want) buffer = new byte[want];
                ReadExactly(stream, buffer, want, name);
                walker.Section(id, pos, length, buffer.AsSpan(0, want));
                pos += 8L + length;
                if (identityOnly) break;
            }
        }
        return walker.Finish();
    }

    // The Alpine Faction release that introduced each level version from 300 on.
    private static readonly string[] AlpineReleases = ["1.0.0", "1.1.0", "1.2.0", "1.2.2", "1.3.0", "1.4.0", "1.5.0"];

    /// <summary>The first Alpine Faction release that loads a level of <paramref name="version"/> (300 and up), or null when not known.</summary>
    public static string? AlpineReleaseFor(int version) =>
        version is >= 300 and <= NewestKnownVersion ? AlpineReleases[version - 300] : null;

    /// <summary>"RF 1.0 (180)", "RED 1.2 (200)", "Alpine 1.5.0 (306)", "PS2 (174)", "unsupported (45)".</summary>
    public static (RflEra Era, string Label) DescribeVersion(int version)
    {
        return version switch
        {
            0xB4 => (RflEra.Stock10, "RF 1.0 (180)"),
            0xC8 => (RflEra.Stock12, "RED 1.2 (200)"),
            0xAE or 0xAF => (RflEra.PlayStation2, $"PS2 ({version})"),
            >= 0x28 and < 0xC8 => (RflEra.PreRelease, $"pre-release ({version})"),
            >= 300 and <= NewestKnownVersion => (RflEra.Alpine, $"Alpine {AlpineReleases[version - 300]} ({version})"),
            > NewestKnownVersion => (RflEra.Alpine, $"Alpine, newer than 1.5.0 ({version})"),
            0x127 => (RflEra.Unknown, "Red Faction II (295), unsupported"),
            _ => (RflEra.Unknown, $"unsupported ({version})"),
        };
    }

    /// <summary>True for the versions RF or Alpine Faction load, whose section layouts this reader knows.</summary>
    public static bool IsSupportedVersion(int version) => version is >= 0x28 and <= 0xC8 or >= 300;

    private static void ReadExactly(Stream stream, Span<byte> buffer, int count, string name)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer[read..count]);
            if (n <= 0) throw new AssetFormatException($"'{name}' ends earlier than its stream length says.");
            read += n;
        }
    }

    internal readonly record struct Header(
        int Version, uint? Timestamp, string? LevelName, string? ModName, long End, string? Problem);

    private static Header ReadHeader(ReadOnlySpan<byte> data, string name, long totalLength)
    {
        if (totalLength == 0) throw new AssetFormatException($"'{name}' is empty.");
        if (data.Length < 4) throw new AssetFormatException($"'{name}' is too short to be a level ({data.Length} bytes).");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic != Magic)
            throw new AssetFormatException($"'{name}' is not a Red Faction level (signature 0x{magic:X8}).");
        if (data.Length < 8) throw new AssetFormatException($"'{name}' is damaged: the header ends after the signature.");
        int version = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var r = new RflSpan(data) { Position = 8 };
        uint? timestamp = null;
        string? levelName = null, modName = null;
        try
        {
            // Each field is present only from the version the game started writing it.
            if (version >= 0x72) timestamp = r.U32();
            r.Skip(8); // player start and level info offsets: never used, the stream is walked instead
            if (version >= 0xA0) r.Skip(8); // section count and total size: wrong in many files
            if (version >= 0xAA) levelName = r.Str();
            if (version >= 0xB2) modName = r.Str();
        }
        catch (RflBodyException e)
        {
            if (IsSupportedVersion(version))
                throw new AssetFormatException($"'{name}' is damaged: the header ends early ({e.Message}).");
            return new Header(version, null, null, null, data.Length, "header could not be read");
        }
        return new Header(version, timestamp, levelName, modName, r.Position, null);
    }

    /// <summary>Collects the summary as sections arrive; shared by the span and stream entry points.</summary>
    private sealed class Walker
    {
        private readonly string _name;
        private readonly Header _header;
        private readonly long _fileSize;
        private readonly int _v;
        private readonly bool _limited;
        private readonly List<string> _notes = [];
        private readonly List<RflSectionInfo> _sections = [];
        private readonly List<RflCount> _counts = [];
        private readonly List<(RflReferenceKind Kind, string Name, string Place)> _pending = [];
        private readonly Dictionary<(RflReferenceKind, string), (string Name, List<string> Places)> _refs = [];
        private readonly List<(RflReferenceKind, string)> _refOrder = [];
        private readonly Dictionary<uint, string[]> _preloads = [];
        private Dictionary<string, int> _entityClasses = new(StringComparer.Ordinal);
        private Dictionary<string, int> _itemClasses = new(StringComparer.Ordinal);
        private Dictionary<string, int> _clutterClasses = new(StringComparer.Ordinal);
        private Dictionary<string, int> _eventClasses = new(StringComparer.Ordinal);
        private bool _truncated, _redPlus, _glacier, _alpineLightmaps, _playerStart;
        private RflLevelProperties? _props;
        private AlpineLevelProperties? _alpine;
        private int? _dash;
        private int? _rooms, _faces, _vertices, _portals, _sky, _liquid, _geometryTextures;
        private int _omni, _spot, _tube, _red, _blue, _bot;
        private string? _infoName, _author, _date;
        private bool? _multiplayer, _hasMovers;

        public Walker(string name, Header header, long fileSize)
        {
            _name = name;
            _header = header;
            _fileSize = fileSize;
            _v = header.Version;
            // Pre-release record layouts are not known well enough to trust names read from them.
            _limited = _v < 0xB4 && _v != 0xAE && _v != 0xAF;
            WalksSections = IsSupportedVersion(_v);
            if (!WalksSections)
                _notes.Add($"Unsupported level version {_v}: only the header was read.");
            else if (_limited)
                _notes.Add($"Pre-release level version {_v}: only section counts and the level info were read.");
            if (header.Problem is not null) _notes.Add($"The header could not be read: {header.Problem}.");
        }

        public bool WalksSections { get; }

        /// <summary>False when the stream ends here (no end marker): reading stops.</summary>
        public bool BeforeSection(long pos, long left)
        {
            if (left <= 0)
            {
                _notes.Add("The file ends without an end marker; everything before that point was read.");
                _truncated = true;
                return false;
            }
            if (left < 8)
            {
                _notes.Add($"The file ends inside a section header at offset {pos}; everything before that point was read.");
                _truncated = true;
                return false;
            }
            return true;
        }

        /// <summary>False at the end marker or when the section claims more bytes than remain.</summary>
        public bool SectionHeader(uint id, int length, long pos, long bodyLeft)
        {
            if (id == 0) return false; // end marker; its length and anything after it are ignored
            if (length < 0 || length > bodyLeft)
            {
                _sections.Add(new RflSectionInfo(id, SectionName(id), pos, length, RflSectionStatus.Truncated,
                    $"claims {length} bytes, {Math.Max(0, bodyLeft)} remain"));
                _notes.Add($"{SectionName(id)} at offset {pos} claims {length} bytes but only {Math.Max(0, bodyLeft)} remain; reading stopped there.");
                _truncated = true;
                return false;
            }
            return true;
        }

        /// <summary>How many leading body bytes <see cref="Section"/> needs: all, the count, or none.</summary>
        public int BodyBytes(uint id, int length) => Plan(id) switch
        {
            BodyPlan.Whole => length,
            BodyPlan.None => 0,
            _ => Math.Min(length, 4),
        };

        private enum BodyPlan { Whole, CountOnly, AlpineCount, None }

        private BodyPlan Plan(uint id)
        {
            if (_limited)
            {
                if (id == 0x01000000) return BodyPlan.Whole;
                return CountLabel(id) is not null ? BodyPlan.CountOnly : BodyPlan.None;
            }
            return id switch
            {
                0x200 or 0x1200 or 0x02000000 or 0x03000000 => BodyPlan.CountOnly,
                >= 0x0AFBAE02 and <= 0x0AFBAE0B and not 0x0AFBAE05 and not 0x0AFBAE09 => BodyPlan.AlpineCount,
                _ when Handled(id) => BodyPlan.Whole,
                _ => BodyPlan.None,
            };
        }

        private static bool Handled(uint id) => id switch
        {
            0x100 or 0x300 or 0x400 or 0x500 or 0x600 or 0x700 or 0x900 or 0xA00 or 0xB00 or 0xC00 or 0xD00
                or 0xE00 or 0xF00 or 0x1000 or 0x1100 or 0x2000 or 0x3000 or 0x4000 or 0x5000 or 0x6000
                or 0x7000 or 0x7001 or 0x7002 or 0x7003 or 0x7004 or 0x8000 or 0x10000 or 0x20000 or 0x30000
                or 0x40000 or 0x50000 or 0x60000 or 0x70000 or 0x01000000 or 0x04000000 or 0x0AFBA5ED
                or 0x0AFBAE01 or 0xDA58FA00 => true,
            _ => false,
        };

        /// <summary>Reads one section; <paramref name="body"/> is the whole body or, for count-only sections, its head.</summary>
        public void Section(uint id, long pos, int length, ReadOnlySpan<byte> body)
        {
            string name = SectionName(id);
            if ((id & 0xFFFF0000) == 0x5ED00000) _redPlus = true;
            if ((id & 0xFFFF0000) == 0x6ED00000) _glacier = true;
            if (id == 0x0AFBAE09) _alpineLightmaps = true;

            var plan = Plan(id);
            if (plan == BodyPlan.None)
            {
                bool known = Handled(id) || id is 0x800 or 0x0AFBAE05 or 0x0AFBAE09 or 0xAFBA5ED1
                    || (id & 0xFFFF0000) is 0x5ED00000 or 0x6ED00000;
                var status = known ? RflSectionStatus.Skipped : RflSectionStatus.Unknown;
                if (!known) _notes.Add($"Unknown section 0x{id:X8} ({length} bytes) at offset {pos} was skipped.");
                _sections.Add(new RflSectionInfo(id, name, pos, length, status, _limited && known ? "older layout" : null));
                return;
            }

            if (plan is BodyPlan.CountOnly or BodyPlan.AlpineCount)
            {
                var head = new RflSpan(body);
                long n = body.Length >= 4 ? (plan == BodyPlan.AlpineCount ? head.U32() : head.I32()) : -1;
                // Every record takes at least one byte, so a larger count is garbage.
                if (n >= 0 && n <= length - 4)
                {
                    AddCount(CountLabel(id) ?? name, (int)n);
                    _sections.Add(new RflSectionInfo(id, name, pos, length, RflSectionStatus.Read, null));
                }
                else if (length == 0)
                {
                    AddCount(CountLabel(id) ?? name, 0);
                    _sections.Add(new RflSectionInfo(id, name, pos, length, RflSectionStatus.Read, "empty"));
                }
                else
                {
                    _notes.Add($"{name}: the record count ({n}) does not fit the section.");
                    _sections.Add(new RflSectionInfo(id, name, pos, length, RflSectionStatus.Damaged, $"count {n} does not fit"));
                }
                return;
            }

            _pending.Clear();
            var r = new RflSpan(body);
            string? detail = null;
            try
            {
                if (length == 0 && CountLabel(id) is { } emptyLabel)
                {
                    // Seen in the wild where a count was expected: treat as an empty list.
                    AddCount(emptyLabel, 0);
                    detail = "empty";
                }
                else
                {
                    detail = ReadBody(id, ref r);
                    if (detail is null && r.Left > 0) detail = $"{r.Left} bytes not read";
                    else if (detail == "") detail = null; // read as far as needed, by design
                }
            }
            catch (RflBodyException e)
            {
                _pending.Clear();
                var head = new RflSpan(body);
                string kept = "";
                if (CountLabel(id) is { } label && body.Length >= 4)
                {
                    int n = head.I32();
                    if (n >= 0 && n <= length - 4)
                    {
                        AddCount(label, n);
                        kept = "; only its count was kept";
                    }
                }
                _notes.Add($"{name} does not match the expected layout ({e.Message}){kept}.");
                _sections.Add(new RflSectionInfo(id, name, pos, length, RflSectionStatus.Damaged, e.Message));
                return;
            }
            foreach (var (kind, refName, place) in _pending) CommitReference(kind, refName, place);
            _pending.Clear();
            _sections.Add(new RflSectionInfo(id, name, pos, length, RflSectionStatus.Read, detail));
        }

        /// <summary>Reads a body; returns a status detail, or null when the body was read as expected.</summary>
        private string? ReadBody(uint id, ref RflSpan r)
        {
            switch (id)
            {
                case 0x01000000: ReadLevelInfo(ref r); return "";
                case 0x100: ReadStaticGeometry(ref r); return "";
                case 0x300: ReadLights(ref r, "Lights", true); return null;
                case 0x04000000: ReadLights(ref r, "Editor-only lights", false); return null;
                case 0x400: AddCount("Cutscene cameras", ObjectList(ref r, 0)); return null;
                case 0xF00: AddCount("Targets", ObjectList(ref r, 0)); return null;
                case 0x5000: AddCount("Cutscene path nodes", ObjectList(ref r, 0)); return null;
                case 0x500: ReadAmbientSounds(ref r); return null;
                case 0x600: return ReadEvents(ref r);
                case 0x700: ReadRespawns(ref r); return null;
                case 0x900: ReadLevelProperties(ref r); return null;
                case 0xA00: ReadParticles(ref r); return null;
                case 0xB00: ReadGasRegions(ref r); return null;
                case 0xC00: ReadRoomEffects(ref r); return null;
                case 0xD00: AddCount("Climbing regions", ObjectList(ref r, 16)); return null;
                case 0xE00: ReadBolts(ref r); return null;
                case 0x1000: ReadDecals(ref r); return null;
                case 0x1100: ReadPushRegions(ref r); return null;
                case 0x2000: ReadMovers(ref r); return null;
                case 0x3000: ReadMovingGroups(ref r); return null;
                case 0x4000: ReadCutscenes(ref r); return null;
                case 0x6000: ReadNamedUidLists(ref r, "Cutscene paths"); return null;
                case 0x10000: ReadNamedUidLists(ref r, "Waypoint lists"); return null;
                case 0x7000: case 0x7001: case 0x7002: case 0x7003: case 0x7004: ReadFileList(id, ref r); return null;
                case 0x8000: ReadEax(ref r); return null;
                case 0x20000: ReadNavPoints(ref r); return null;
                case 0x30000: ReadEntities(ref r); return null;
                case 0x40000: ReadItems(ref r); return null;
                case 0x50000: ReadClutter(ref r); return null;
                case 0x60000: ReadTriggers(ref r); return null;
                case 0x70000: r.Skip(48); _playerStart = true; return null;
                case 0x0AFBA5ED: ReadAlpineProperties(ref r); return "";
                case 0x0AFBAE01: return ReadAlpineMeshes(ref r);
                case 0xDA58FA00:
                    if (r.U32() == 1) _dash = r.U8();
                    return "";
                default: return "";
            }
        }

        // ---------- helpers ----------

        private void AddCount(string label, int n)
        {
            for (int i = 0; i < _counts.Count; i++)
            {
                if (_counts[i].Label == label)
                {
                    _counts[i] = _counts[i] with { Count = _counts[i].Count + n };
                    return;
                }
            }
            _counts.Add(new RflCount(label, n));
        }

        private void Reference(RflReferenceKind kind, string name, string place)
        {
            name = name.Trim();
            // RED writes "userbmap"-style placeholders and empty slots; only file names count.
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1) return;
            _pending.Add((kind, name, place));
        }

        private void CommitReference(RflReferenceKind kind, string name, string place)
        {
            var key = (kind, name.ToLowerInvariant());
            if (!_refs.TryGetValue(key, out var entry))
            {
                entry = (name, []);
                _refs[key] = entry;
                _refOrder.Add(key);
            }
            if (!entry.Places.Contains(place)) entry.Places.Add(place);
        }

        /// <summary>The common object prefix; returns the class name.</summary>
        private static string ObjectHead(ref RflSpan r)
        {
            r.Skip(4);
            string cls = r.Str();
            r.Skip(48);
            r.SkipStr();
            r.Skip(1);
            return cls;
        }

        private static int ObjectList(ref RflSpan r, int tail)
        {
            int n = r.Count(57);
            for (int i = 0; i < n; i++)
            {
                ObjectHead(ref r);
                r.Skip(tail);
            }
            return n;
        }

        private static void Tally(Dictionary<string, int> map, string key) =>
            map[key] = map.TryGetValue(key, out int c) ? c + 1 : 1;

        // ---------- sections ----------

        private void ReadLevelInfo(ref RflSpan r)
        {
            // Only the leading fields: the editor views after them are garbage in some files.
            r.Skip(4);
            _infoName = r.Str();
            _author = r.Str();
            _date = r.Str();
            _hasMovers = r.Bool();
            _multiplayer = r.Bool();
        }

        private void ReadGeometry(ref RflSpan r, bool full, string texturePlace, out int faces, out int textures)
        {
            if (_v >= 0xC8) r.Skip(8);
            r.SkipStr();
            if (_v < 0xC8) r.Skip(4);
            textures = r.Count(2);
            for (int i = 0; i < textures; i++) Reference(RflReferenceKind.Texture, r.Str(), texturePlace);
            int scrolls = r.Count(_v >= 0xB4 ? 12 : 41);
            r.Skip((long)scrolls * (_v >= 0xB4 ? 12 : 41));
            int rooms = r.Count(40), sky = 0, liquid = 0;
            for (int i = 0; i < rooms; i++)
            {
                r.Skip(28);
                bool isSky = r.Bool();
                r.Skip(3);
                bool isLiquid = r.Bool();
                bool hasAmbient = r.Bool();
                r.Skip(2 + 4);
                if (_v >= 0xB4) r.SkipStr();
                if (isLiquid)
                {
                    r.Skip(8);
                    Reference(RflReferenceKind.Texture, r.Str(), "liquid surface");
                    r.Skip(37);
                    liquid++;
                }
                if (hasAmbient) r.Skip(4);
                if (isSky) sky++;
            }
            int subrooms = r.Count(8);
            for (int i = 0; i < subrooms; i++)
            {
                r.Skip(4);
                r.Skip(4L * r.Count(4));
            }
            int portals = r.Count(32);
            r.Skip(32L * portals);
            int vertices = r.Count(12);
            r.Skip(12L * vertices);
            faces = r.Count(1);
            if (!full)
            {
                _rooms = rooms; _sky = sky; _liquid = liquid; _portals = portals; _vertices = vertices; _faces = faces;
                _geometryTextures = textures;
                return;
            }
            for (int i = 0; i < faces; i++)
            {
                r.Need(56);
                r.Skip(20);
                int surface = r.I32();
                r.Skip(28);
                int k = r.Count(12);
                // The game keeps the surface index as 16 bits and reads lightmap UVs only when that is >= 0.
                r.Skip((long)k * ((short)(surface & 0xFFFF) >= 0 ? 20 : 12));
            }
            r.Skip(96L * r.Count(96));
            if (_v <= 0xB4) r.Skip(12L * r.Count(12));
        }

        private void ReadStaticGeometry(ref RflSpan r) => ReadGeometry(ref r, false, "level geometry", out _, out _);

        private void ReadMovers(ref RflSpan r)
        {
            int n = r.Count(1);
            for (int i = 0; i < n; i++)
            {
                r.Skip(52);
                ReadGeometry(ref r, true, "mover geometry", out _, out _);
                r.Skip(12);
            }
            AddCount("Movers", n);
        }

        private void ReadLights(ref RflSpan r, string label, bool inGame)
        {
            int n = r.Count(57);
            int omni = 0, spot = 0, tube = 0;
            for (int i = 0; i < n; i++)
            {
                ObjectHead(ref r);
                uint flags = r.U32();
                r.Skip(4 + 16 + 4 + 28);
                switch ((flags >> 4) & 3)
                {
                    case 1: omni++; break;
                    case 2: spot++; break;
                    case 3: tube++; break;
                }
            }
            AddCount(label, n);
            if (inGame) { _omni += omni; _spot += spot; _tube += tube; }
        }

        private void ReadAmbientSounds(ref RflSpan r)
        {
            int n = r.Count(1);
            for (int i = 0; i < n; i++)
            {
                r.Skip(17);
                Reference(RflReferenceKind.Sound, r.Str(), "ambient sound");
                r.Skip(16);
            }
            AddCount("Ambient sounds", n);
        }

        private static readonly HashSet<string> FileEventClasses = new(StringComparer.Ordinal)
        {
            "Switch_Model", "Play_Sound", "Music_Start", "Swap_Textures", "Display_Fullscreen_Image", "Load_Level",
            "Play_Animation", "Alarm", "Play_Video", "Mesh_Animate", "Set_Debris", "AF_Fullscreen_Image",
            "Mesh_Set_Texture", "World_HUD_Sprite",
        };

        private string? ReadEvents(ref RflSpan r)
        {
            int start = r.Position;
            try
            {
                ReadEventRecords(ref r, alpineOrientation: true);
                return null;
            }
            catch (RflBodyException) when (_v >= 300)
            {
                // Some pre-release Alpine files store AF_Teleport_Player without its orientation.
                r.Position = start;
                _pending.Clear();
                ReadEventRecords(ref r, alpineOrientation: false);
                return "Alpine teleport events without orientation";
            }
        }

        private void ReadEventRecords(ref RflSpan r, bool alpineOrientation)
        {
            int n = r.Count(1);
            var classes = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                r.Skip(4);
                string cls = r.Str();
                r.Skip(12);
                r.SkipStr();
                r.Skip(1 + 4 + 2 + 16);
                string s1 = r.Str(), s2 = r.Str();
                r.Skip(4L * r.Count(4));
                bool orient = (_v >= 0x91 && cls is "Teleport" or "Play_Vclip" or "Teleport_Player")
                    || (_v >= 0x98 && cls == "Alarm")
                    || (alpineOrientation && _v >= 300 && cls is "AF_Teleport_Player" or "Clone_Entity")
                    || (alpineOrientation && _v >= 301 && cls == "Anchor_Marker_Orient");
                if (orient) r.Skip(36);
                if (_v >= 0xB0) r.Skip(4);
                Tally(classes, cls);
                if (FileEventClasses.Contains(cls))
                {
                    EventFile(cls, s1);
                    EventFile(cls, s2);
                }
            }
            // A strict walk must end exactly at the body's end, or the layout guess was wrong.
            if (r.Left != 0) throw new RflBodyException($"{r.Left} bytes left after the events");
            _eventClasses = classes;
            AddCount("Events", n);
        }

        private void EventFile(string cls, string value)
        {
            int dot = value.LastIndexOf('.');
            if (dot <= 0) return;
            var kind = KindOfExtension(value[(dot + 1)..]);
            if (kind is null) return;
            if (cls == "Music_Start" && kind == RflReferenceKind.Sound) kind = RflReferenceKind.Music;
            Reference(kind.Value, value, "event " + cls);
        }

        /// <summary>The kind of asset a file extension (without the dot) names, or null.</summary>
        public static RflReferenceKind? KindOfExtension(string ext) => ext.ToLowerInvariant() switch
        {
            "tga" or "vbm" or "dds" or "png" or "jpg" or "jpeg" or "bmp" or "atx" => RflReferenceKind.Texture,
            "v3m" or "v3c" or "v3d" => RflReferenceKind.Mesh,
            "rfa" or "mvf" => RflReferenceKind.Clip,
            "vfx" => RflReferenceKind.Effect,
            "wav" or "ogg" or "mp3" or "aif" or "aiff" => RflReferenceKind.Sound,
            "bik" => RflReferenceKind.Video,
            "rfl" => RflReferenceKind.Level,
            _ => null,
        };

        private void ReadRespawns(ref RflSpan r)
        {
            int n = r.Count(1), red = 0, blue = 0, bot = 0;
            for (int i = 0; i < n; i++)
            {
                r.Skip(52);
                r.SkipStr();
                if (_v < 67) r.SkipStr();
                r.Skip(5);
                if (_v >= 0xAC)
                {
                    if (r.Bool()) red++;
                    if (r.Bool()) blue++;
                    if (r.Bool()) bot++;
                }
            }
            AddCount("Respawn points", n);
            _red += red; _blue += blue; _bot += bot;
        }

        private void ReadLevelProperties(ref RflSpan r)
        {
            string geomod = r.Str();
            int hardness = r.I32();
            var ambient = r.Color();
            bool directional = r.Bool();
            var fog = r.Color();
            float near = r.F32(), far = r.F32();
            _props = new RflLevelProperties(geomod, hardness, ambient, directional, fog, near, far);
            Reference(RflReferenceKind.Texture, geomod, "geomod texture");
        }

        private void ReadParticles(ref RflSpan r)
        {
            int n = r.Count(57);
            for (int i = 0; i < n; i++)
            {
                ObjectHead(ref r);
                r.Skip(16);
                Reference(RflReferenceKind.Texture, r.Str(), "particle emitter");
                r.Skip(48 + 8 + 4 + 2 + 2 + 1 + 20);
            }
            AddCount("Particle emitters", n);
        }

        private void ReadGasRegions(ref RflSpan r)
        {
            int n = r.Count(57);
            for (int i = 0; i < n; i++)
            {
                ObjectHead(ref r);
                int shape = r.I32();
                if (shape == 1) r.Skip(4);
                else if (shape == 2) r.Skip(12);
                r.Skip(8);
            }
            AddCount("Gas regions", n);
        }

        private void ReadRoomEffects(ref RflSpan r)
        {
            int n = r.Count(1);
            for (int i = 0; i < n; i++)
            {
                int type = r.I32();
                if (type == 3) r.Skip(4);
                if (type == 2)
                {
                    r.Skip(8);
                    Reference(RflReferenceKind.Texture, r.Str(), "liquid room effect");
                    r.Skip(4 + 4 + 4 + 1 + 4 + 4 + 4 + 8);
                }
                r.Skip(3);
                ObjectHead(ref r);
            }
            AddCount("Room effects", n);
        }

        private void ReadBolts(ref RflSpan r)
        {
            int n = r.Count(57);
            for (int i = 0; i < n; i++)
            {
                ObjectHead(ref r);
                r.Skip(4 + 16 + 4 + 16 + 4);
                Reference(RflReferenceKind.Texture, r.Str(), "bolt emitter");
                r.Skip(5);
            }
            AddCount("Bolt emitters", n);
        }

        private void ReadDecals(ref RflSpan r)
        {
            int n = r.Count(57);
            for (int i = 0; i < n; i++)
            {
                ObjectHead(ref r);
                r.Skip(12);
                Reference(RflReferenceKind.Texture, r.Str(), "decal");
                r.Skip(13);
            }
            AddCount("Decals", n);
        }

        private void ReadPushRegions(ref RflSpan r)
        {
            int n = r.Count(57);
            for (int i = 0; i < n; i++)
            {
                ObjectHead(ref r);
                int shape = r.I32();
                r.Skip(shape == 1 ? 4 : 12);
                r.Skip(8);
            }
            AddCount("Push regions", n);
        }

        private void ReadMovingGroups(ref RflSpan r)
        {
            int n = r.Count(1), keyframes = 0;
            for (int i = 0; i < n; i++)
            {
                r.SkipStr();
                r.Skip(1);
                if (r.Bool())
                {
                    int k = r.Count(1);
                    for (int j = 0; j < k; j++)
                    {
                        r.Skip(52);
                        r.SkipStr();
                        r.Skip(1 + 20 + 12 + 4);
                    }
                    keyframes += k;
                    r.Skip(52L * r.Count(52));
                    r.Skip(6 + 8);
                    for (int s = 0; s < 4; s++)
                    {
                        Reference(RflReferenceKind.Sound, r.Str(), "moving group sound");
                        r.Skip(4);
                    }
                }
                r.Skip(4L * r.Count(4));
                r.Skip(4L * r.Count(4));
            }
            AddCount("Moving groups", n);
            AddCount("Keyframes", keyframes);
        }

        private void ReadCutscenes(ref RflSpan r)
        {
            int n = r.Count(1);
            for (int i = 0; i < n; i++)
            {
                r.Skip(9);
                int k = r.Count(1);
                for (int j = 0; j < k; j++)
                {
                    r.Skip(24);
                    r.SkipStr();
                }
            }
            AddCount("Cutscenes", n);
        }

        private void ReadNamedUidLists(ref RflSpan r, string label)
        {
            int n = r.Count(1);
            for (int i = 0; i < n; i++)
            {
                r.SkipStr();
                r.Skip(4L * r.Count(4));
            }
            AddCount(label, n);
        }

        private void ReadFileList(uint id, ref RflSpan r)
        {
            int n = r.Count(2);
            var names = new List<string>();
            for (int i = 0; i < n; i++) names.Add(r.Str());
            if (id != 0x7000) r.Skip(4L * n);
            _preloads[id] = [.. names];
        }

        private void ReadEax(ref RflSpan r)
        {
            int n = r.Count(1);
            for (int i = 0; i < n; i++)
            {
                r.SkipStr();
                ObjectHead(ref r);
            }
            AddCount("EAX effects", n);
        }

        private void ReadNavPoints(ref RflSpan r)
        {
            int n = r.Count(1);
            for (int i = 0; i < n; i++)
            {
                r.Skip(4 + 1 + 4 + 12 + 4 + 4);
                if (r.Bool()) r.Skip(36);
                r.Skip(3 + 4);
                r.Skip(4L * r.Count(4));
            }
            for (int i = 0; i < n; i++) r.Skip(4L * r.U8());
            AddCount("Nav points", n);
        }

        private void ReadEntities(ref RflSpan r)
        {
            int n = r.Count(57);
            var classes = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                Tally(classes, ObjectHead(ref r));
                r.Skip(12);
                r.SkipStr();
                r.SkipStr();
                r.Skip(6 + 8 + 3 + 8 + 4);
                for (int s = 0; s < 7; s++) r.SkipStr();
                r.Skip(2 + 16 + 16);
                if (r.Bool()) r.Skip(4);
                r.SkipStr();
                r.SkipStr();
            }
            AddCount("Entities", n);
            _entityClasses = classes;
        }

        private void ReadItems(ref RflSpan r)
        {
            int n = r.Count(57);
            var classes = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                Tally(classes, ObjectHead(ref r));
                r.Skip(12);
            }
            AddCount("Items", n);
            _itemClasses = classes;
        }

        private void ReadClutter(ref RflSpan r)
        {
            int n = r.Count(57);
            var classes = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                Tally(classes, ObjectHead(ref r));
                r.Skip(4);
                r.SkipStr();
                r.Skip(4L * r.Count(4));
            }
            AddCount("Clutter", n);
            _clutterClasses = classes;
        }

        private void ReadTriggers(ref RflSpan r)
        {
            int n = r.Count(1);
            for (int i = 0; i < n; i++)
            {
                r.Skip(4);
                r.SkipStr();
                r.Skip(1);
                int shape = r.I32();
                r.Skip(4 + 4 + 1);
                r.SkipStr();
                r.Skip(5 + 12);
                r.Skip(shape == 0 ? 4 : 36 + 12 + 1);
                r.Skip(12 + 1 + 8);
                if (_v >= 0xB1) r.Skip(4);
                r.Skip(4L * r.Count(4));
            }
            AddCount("Triggers", n);
        }

        private void ReadAlpineProperties(ref RflSpan r)
        {
            // Fields were appended without raising the chunk version, so read field by field and keep
            // whatever was there when the body runs out.
            var a = new AlpineLevelProperties { ChunkVersion = r.U32() };
            uint ver = a.ChunkVersion;
            bool early = true;
            try
            {
                if (ver >= 1) a = a with { LegacyCyclicTimers = r.Bool() };
                if (ver >= 2)
                {
                    a = a with { LegacyMovers = r.Bool() };
                    a = a with { StartsWithHeadlamp = r.Bool() };
                }
                if (ver >= 3)
                {
                    a = a with { OverrideStaticMeshAmbient = r.Bool() };
                    a = a with { StaticMeshAmbientModifier = r.F32() };
                }
                if (ver >= 4)
                {
                    a = a with { Rf2StyleGeomod = r.Bool() };
                    a = a with { GeoableBrushes = r.SkipU32List(8) };
                    a = a with { BreakableBrushes = r.SkipU32List(9) };
                    a = a with { HoldOpenKeyframes = r.SkipU32List(4) };
                }
                if (ver >= 5)
                {
                    a = a with { SunEnabled = r.Bool() };
                    a = a with { SunYaw = r.F32() };
                    a = a with { SunPitch = r.F32() };
                    a = a with { SunColor = r.Color() };
                    a = a with { SunIntensity = r.F32() };
                    a = a with { SunSpread = r.F32() };
                    a = a with { SunCastsBakedShadows = r.Bool() };
                    a = a with { SunAffectsMeshes = r.Bool() };
                    a = a with { SunMeshMode = r.U8() };
                    a = a with { SunDrivesShadowMap = r.Bool() };
                    a = a with { LegacyLighting = r.Bool() };
                    a = a with { HighResolutionLightmaps = r.Bool() };
                    a = a with { LiquidOccludes = r.Bool() };
                    a = a with { InvisibleFacesOcclude = r.Bool() };
                    a = a with { AlphaFacesOcclude = r.Bool() };
                    a = a with { NoShadowBrushes = r.SkipU32List(4) };
                    a = a with { MeshesOcclude = r.Bool() };
                    a = a with { LightmapDensity = r.U8() };
                    a = a with { LightmapFlags = r.U8() };
                    a = a with { LightmapCompression = r.U8() };
                }
                if (ver >= 6)
                {
                    a = a with { FlightCeilingEnabled = r.Bool() };
                    a = a with { FlightCeilingY = r.F32() };
                    a = a with { MinimapEnabled = r.Bool() };
                    a = a with { MinimapBitmap = r.Str() };
                    r.Skip(24);
                    a = a with { MinimapCutHeight = r.F32() };
                }
                early = false;
            }
            catch (RflBodyException)
            {
            }
            _alpine = a with { EndedEarly = early };
            if (!string.IsNullOrEmpty(a.MinimapBitmap)) Reference(RflReferenceKind.Texture, a.MinimapBitmap, "Alpine minimap");
        }

        private enum MeshTail { Full, NoCorpse, None, ByteMaterial }

        private string? ReadAlpineMeshes(ref RflSpan r)
        {
            uint n = r.U32();
            if (n > 10_000 || n > r.Left) throw new RflBodyException($"mesh count {n} is not plausible");
            int start = r.Position;
            // The record tail changed between builds without a version: try each layout and keep the
            // first that ends exactly at the end of the body.
            foreach (var tail in new[] { MeshTail.Full, MeshTail.NoCorpse, MeshTail.None, MeshTail.ByteMaterial })
            {
                r.Position = start;
                _pending.Clear();
                try
                {
                    WalkMeshes(ref r, (int)n, tail);
                }
                catch (RflBodyException)
                {
                    continue;
                }
                if (r.Left == 0)
                {
                    AddCount("Alpine meshes", (int)n);
                    return tail == MeshTail.Full ? null : $"layout: {tail}";
                }
            }
            _pending.Clear();
            AddCount("Alpine meshes", (int)n);
            _notes.Add("Alpine meshes: no known record layout fits; only the count was kept.");
            r.Position = r.Length;
            return "no known layout fits; count only";
        }

        private void WalkMeshes(ref RflSpan r, int n, MeshTail tail)
        {
            for (int i = 0; i < n; i++)
            {
                r.Skip(52);
                r.SkipStr();
                string file = r.Str();
                var fileKind = KindOfExtension(Path.GetExtension(file).TrimStart('.')) ?? RflReferenceKind.Mesh;
                Reference(fileKind, file, "Alpine mesh");
                Reference(RflReferenceKind.Clip, r.Str(), "Alpine mesh animation");
                r.Skip(1);
                int overrides = r.U8();
                for (int k = 0; k < overrides; k++)
                {
                    r.Skip(1);
                    Reference(RflReferenceKind.Texture, r.Str(), "Alpine mesh texture override");
                }
                if (tail == MeshTail.None) continue;
                r.Skip(tail == MeshTail.ByteMaterial ? 1 : 4);
                if (r.Bool())
                {
                    r.Skip(4);
                    r.SkipStr();
                    r.SkipStr();
                    r.Skip(8 + 44);
                    if (tail == MeshTail.Full)
                    {
                        r.SkipStr();
                        r.SkipStr();
                        r.Skip(2);
                    }
                }
            }
            // Version 306 appends per-mesh flags and then per-mesh collision sources after the records.
            if (r.Left > 0 && _v >= 306 && r.Left >= n)
            {
                r.Skip(n);
                if (r.Left >= 3L * n)
                {
                    for (int i = 0; i < n; i++)
                    {
                        r.Skip(1);
                        r.SkipStr();
                    }
                }
            }
        }

        // ---------- result ----------

        public RflSummary Finish()
        {
            var (era, label) = DescribeVersion(_v);
            DateTimeOffset? saved = null;
            if (_header.Timestamp is { } ts and not 0)
            {
                saved = DateTimeOffset.FromUnixTimeSeconds(ts);
                if (saved.Value.Year < 1998 || saved.Value > DateTimeOffset.UtcNow.AddDays(2))
                    _notes.Add($"The save time ({saved.Value:yyyy-MM-dd HH:mm} UTC) is not plausible.");
            }
            string? headerName = NullIfEmpty(_header.LevelName);
            string? levelName = NullIfEmpty(_infoName) ?? headerName;
            var references = new List<RflReference>(_refOrder.Count);
            foreach (var key in _refOrder)
            {
                var (refName, places) = _refs[key];
                references.Add(new RflReference(refName, key.Item1, places));
            }
            return new RflSummary
            {
                Name = _name,
                FileSize = _fileSize,
                Version = _v,
                Era = era,
                VersionLabel = label,
                SavedUtc = saved,
                LevelName = levelName,
                HeaderLevelName = headerName is not null && headerName != levelName ? headerName : null,
                Author = NullIfEmpty(_author),
                DateText = NullIfEmpty(_date),
                ModName = NullIfEmpty(_header.ModName),
                IsMultiplayer = _multiplayer,
                HasMovers = _hasMovers,
                Properties = _props,
                Alpine = _alpine,
                DashLightmapsFullDepth = _dash,
                HasAlpineLightmaps = _alpineLightmaps,
                HasRedPlusData = _redPlus,
                HasGlacierData = _glacier,
                Rooms = _rooms,
                Faces = _faces,
                Vertices = _vertices,
                Portals = _portals,
                SkyRooms = _sky,
                LiquidRooms = _liquid,
                GeometryTextures = _geometryTextures,
                Counts = [.. _counts],
                OmniLights = _omni,
                SpotLights = _spot,
                TubeLights = _tube,
                RespawnRed = _red,
                RespawnBlue = _blue,
                RespawnBot = _bot,
                HasPlayerStart = _playerStart,
                EntityClasses = _entityClasses,
                ItemClasses = _itemClasses,
                ClutterClasses = _clutterClasses,
                EventClasses = _eventClasses,
                References = references,
                Preloads = new RflPreloads(Preload(0x7000), Preload(0x7001), Preload(0x7002), Preload(0x7003), Preload(0x7004)),
                Sections = [.. _sections],
                Notes = [.. _notes],
                IsTruncated = _truncated,
            };
        }

        private IReadOnlyList<string> Preload(uint id) => _preloads.TryGetValue(id, out var list) ? list : [];

        private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>The label a section's record count is shown under, or null when its first value is not a count.</summary>
    private static string? CountLabel(uint id) => id switch
    {
        0x200 => "Geo regions",
        0x300 => "Lights",
        0x400 => "Cutscene cameras",
        0x500 => "Ambient sounds",
        0x600 => "Events",
        0x700 => "Respawn points",
        0xA00 => "Particle emitters",
        0xB00 => "Gas regions",
        0xC00 => "Room effects",
        0xD00 => "Climbing regions",
        0xE00 => "Bolt emitters",
        0xF00 => "Targets",
        0x1000 => "Decals",
        0x1100 => "Push regions",
        0x1200 => "Lightmaps",
        0x2000 => "Movers",
        0x3000 => "Moving groups",
        0x4000 => "Cutscenes",
        0x5000 => "Cutscene path nodes",
        0x6000 => "Cutscene paths",
        0x8000 => "EAX effects",
        0x10000 => "Waypoint lists",
        0x20000 => "Nav points",
        0x30000 => "Entities",
        0x40000 => "Items",
        0x50000 => "Clutter",
        0x60000 => "Triggers",
        0x02000000 => "Brushes",
        0x03000000 => "Groups",
        0x04000000 => "Editor-only lights",
        0x0AFBAE01 => "Alpine meshes",
        0x0AFBAE02 => "Alpine editor notes",
        0x0AFBAE03 => "Alpine coronas",
        0x0AFBAE04 => "Alpine bags",
        0x0AFBAE06 => "Alpine weather regions",
        0x0AFBAE07 => "Alpine vehicle factories",
        0x0AFBAE08 => "Alpine projection cameras",
        0x0AFBAE0A => "Alpine rope emitters",
        0x0AFBAE0B => "Alpine terrain",
        _ => null,
    };

    /// <summary>A readable name for a section id.</summary>
    public static string SectionName(uint id) => CountLabel(id) ?? id switch
    {
        0x100 => "Static geometry",
        0x800 => "PS2 data",
        0x900 => "Level properties",
        0x7000 => "Bitmap preloads",
        0x7001 => "Character mesh preloads",
        0x7002 => "Animation preloads",
        0x7003 => "Static mesh preloads",
        0x7004 => "Effect preloads",
        0x70000 => "Player start",
        0x01000000 => "Level info",
        0x0AFBA5ED => "Alpine level properties",
        0xAFBA5ED1 => "Alpine level properties (pre-release)",
        0x0AFBAE05 => "Alpine group metadata",
        0x0AFBAE09 => "Alpine lightmaps",
        0xDA58FA00 => "Dash Faction properties",
        _ when (id & 0xFFFF0000) == 0x5ED00000 => $"RED+ data (0x{id:X8})",
        _ when (id & 0xFFFF0000) == 0x6ED00000 => $"Glacier data (0x{id:X8})",
        _ => $"Section 0x{id:X8}",
    };
}
