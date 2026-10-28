using System.Buffers.Binary;

namespace Cairn.Formats.Rfl;

/// <summary>
/// The Red Faction II side of the summary reader: version 0x127 levels, whose first section is the RF2 bitmap list.
/// The layouts are those of research/notes/rf2-rfl-format.md (checked on all 105 levels of RF2's All_Levels): records
/// RF2 shares with RF1 (lights, ambient sounds, respawn points, waypoint lists, the player start, the level info) use
/// the RF1 readers; geometry, level properties, events, decals, push regions, movers, moving groups, coronas, items,
/// clutter, triggers and the resource lists have their own. Sections whose layout is not known are counted where their
/// first value is a record count (object lists are checked by finding each record's object head), else skipped.
/// </summary>
public static partial class RflReader
{
    /// <summary>The first section of every real Red Faction II level (its bitmap list).</summary>
    private const uint Rf2FirstSection = 0x7000;

    /// <summary>A display name for a Red Faction II section (RF2 reuses some RF1 ids for other data).</summary>
    public static string Rf2SectionName(uint id) => Rf2CountLabel(id) ?? id switch
    {
        0x100 => "Static geometry",
        0x900 => "Level properties",
        0x7000 => "Bitmap list",
        0x7001 => "Character mesh list",
        0x7002 => "Animation list",
        0x7003 => "Static mesh list",
        0x7004 => "Effect list",
        0x7005 => "Texture pack list",
        0x7900 => "Vertex lighting",
        0x70000 => "Player start",
        0x01000000 => "Level info",
        _ => $"RF2 section 0x{id:X}",
    };

    /// <summary>The label a Red Faction II section's record count is shown under, or null.</summary>
    private static string? Rf2CountLabel(uint id) => id switch
    {
        0x300 => "Lights",
        0x400 => "Cutscene cameras",
        0x500 => "Ambient sounds",
        0x600 => "Events",
        0x700 => "Respawn points",
        0xC00 => "Room effects",
        0xD00 => "Climbing regions",
        0xE00 => "Bolt emitters",
        0x1000 => "Decals",
        0x1100 => "Push regions",
        0x2000 => "Movers",
        0x3000 => "Moving groups",
        0x4000 => "Cutscenes",
        0x7677 => "Mirrors",
        0x7678 => "Coronas",
        0x7680 => "Destructible brushes",
        0x7682 => "Editor notes",
        0x7777 => "Conversations",
        0x7778 => "Event bundles",
        0x7779 => "Spline paths",
        0x10000 => "Waypoint lists",
        0x20000 => "Nav points",
        0x30000 => "Entities",
        0x40000 => "Items",
        0x50000 => "Clutter",
        0x60000 => "Triggers",
        0x02000000 => "Brushes",
        0x03000000 => "Groups",
        _ => null,
    };

    private sealed partial class Walker
    {
        // The vertex lighting header: version, texture name count, group count.
        private const int Rf2HeadBytes = 12;
        private const string LayoutNotKnown = "count only; layout not known";

        private readonly bool _rf2;
        private bool _rf2Started;
        private RflRf2Properties? _rf2Props;

        /// <summary>False (with a note) when a version 295 file does not start with RF2's bitmap list: an RF1 level with an edited version.</summary>
        private bool Rf2FirstSectionOk(uint id)
        {
            if (_rf2Started) return true;
            _rf2Started = true;
            if (id == Rf2FirstSection) return true;
            _notes.Add($"Version 295 is Red Faction II's, but the first section (0x{id:X8}) is not a Red Faction II level's; only the header was read.");
            return false;
        }

        // Object lists whose records start with an object head (uid, class, position, orientation, script name, hidden)
        // but whose layout after it is not known: counted, and checked by finding every record's head.
        private static bool Rf2Scanned(uint id) => id is 0x400 or 0xD00 or 0xE00 or 0x7677 or 0x7680 or 0x7682 or 0x7777 or 0x7778 or 0x7779 or 0x30000;

        private BodyPlan Rf2Plan(uint id) => id switch
        {
            0x02000000 or 0x03000000 or 0x20000 or 0xC00 or 0x4000 => BodyPlan.CountOnly,
            0x7900 => BodyPlan.Head,
            0x100 or 0x300 or 0x500 or 0x600 or 0x700 or 0x900 or 0x1000 or 0x1100 or 0x2000 or 0x3000 or (>= 0x7000 and <= 0x7005)
                or 0x7678 or 0x7901 or 0x10000 or 0x40000 or 0x50000 or 0x60000 or 0x70000 or 0x01000000 => BodyPlan.Whole,
            _ when Rf2Scanned(id) => BodyPlan.Whole,
            _ => BodyPlan.None,
        };

        private void Rf2Section(uint id, long pos, int length, ReadOnlySpan<byte> body)
        {
            string name = Rf2SectionName(id);
            var plan = Rf2Plan(id);
            if (plan == BodyPlan.None)
            {
                // seen in every real level, but nobody knows their layout
                bool known = id is 0x200 or 0x7681 or 0x01000001;
                if (!known) _notes.Add($"Unknown section 0x{id:X8} ({length} bytes) at offset {pos} was skipped.");
                _sections.Add(new RflSectionInfo(id, name, pos, length, known ? RflSectionStatus.Skipped : RflSectionStatus.Unknown, known ? "layout not known" : null));
                return;
            }
            if (plan == BodyPlan.Head)
            {
                string? info = null;
                if (body.Length >= Rf2HeadBytes)
                {
                    int version = BinaryPrimitives.ReadInt32LittleEndian(body);
                    int names = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                    int groups = BinaryPrimitives.ReadInt32LittleEndian(body[8..]);
                    info = $"version {version}, {names} textures, {groups} groups";
                }
                _sections.Add(new RflSectionInfo(id, name, pos, length, RflSectionStatus.Skipped, info));
                return;
            }
            string label = Rf2CountLabel(id) ?? name;
            if (plan == BodyPlan.CountOnly)
            {
                string? detail = id == 0x02000000 ? null : LayoutNotKnown;
                int n = body.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(body) : -1;
                if (n >= 0 && n <= length - 4)
                {
                    AddCount(label, n);
                    _sections.Add(new RflSectionInfo(id, name, pos, length, RflSectionStatus.Read, detail));
                }
                else if (length == 0)
                {
                    AddCount(label, 0);
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
            string? read;
            try
            {
                if (length == 0)
                {
                    if (Rf2CountLabel(id) is { } emptyLabel) AddCount(emptyLabel, 0);
                    read = "empty";
                }
                else
                {
                    read = Rf2Body(id, ref r);
                    if (read is null && r.Left > 0) read = $"{r.Left} bytes not read";
                    else if (read == "") read = null;
                }
            }
            catch (RflBodyException e)
            {
                _pending.Clear();
                string kept = "";
                if (Rf2CountLabel(id) is { } countLabel && body.Length >= 4)
                {
                    int n = BinaryPrimitives.ReadInt32LittleEndian(body);
                    if (n >= 0 && n <= length - 4)
                    {
                        AddCount(countLabel, n);
                        kept = "; only its count was kept";
                    }
                }
                _notes.Add($"{name} does not match the Red Faction II layout ({e.Message}){kept}.");
                _sections.Add(new RflSectionInfo(id, name, pos, length, RflSectionStatus.Damaged, e.Message));
                return;
            }
            foreach (var (kind, refName, place) in _pending) CommitReference(kind, refName, place);
            _pending.Clear();
            _sections.Add(new RflSectionInfo(id, name, pos, length, RflSectionStatus.Read, read));
        }

        /// <summary>Reads an RF2 body; returns a status detail, "" when read as far as needed, or null when read exactly.</summary>
        private string? Rf2Body(uint id, ref RflSpan r)
        {
            switch (id)
            {
                case 0x01000000: Rf2LevelInfo(ref r); return "";
                case 0x100: Rf2Geometry(ref r, "level geometry", statistics: true); return "";
                case 0x300: ReadLights(ref r, "Lights", true); return null;
                case 0x500: ReadAmbientSounds(ref r); return null;
                case 0x600: Rf2Events(ref r); return null;
                case 0x700: ReadRespawns(ref r); return null;
                case 0x900: Rf2LevelProperties(ref r); return null;
                case 0x1000: Rf2Decals(ref r); return null;
                case 0x1100: Rf2PushRegions(ref r); return null;
                case 0x2000: Rf2Movers(ref r); return null;
                case 0x3000: Rf2MovingGroups(ref r); return null;
                case >= 0x7000 and <= 0x7005: Rf2FileList(id, ref r); return null;
                case 0x7678: Rf2Coronas(ref r); return null;
                case 0x7901: return $"value {r.U8()}";
                case 0x10000: ReadNamedUidLists(ref r, "Waypoint lists"); return null;
                case 0x40000: Rf2Items(ref r); return null;
                case 0x50000: Rf2Clutter(ref r); return null;
                case 0x60000: Rf2Triggers(ref r); return null;
                case 0x70000: r.Skip(48); _playerStart = true; return null;
                default:
                    return Rf2Scanned(id) ? Rf2Scan(id, ref r) : "";
            }
        }

        /// <summary>The RF2 object head; returns the class name.</summary>
        private static string Rf2Head(ref RflSpan r)
        {
            r.Skip(4);
            string cls = r.Rf2Str();
            r.Skip(48);
            r.Rf2Str();
            r.Skip(1);
            return cls;
        }

        private void Rf2LevelInfo(ref RflSpan r)
        {
            // The leading fields only: four editor views and 2,080 filler bytes follow.
            r.Skip(4);
            _infoName = r.Rf2Str();
            _author = r.Rf2Str();
            _date = r.Rf2Str();
            _hasMovers = r.Bool();
            _multiplayer = r.Bool();
        }

        /// <summary>
        /// The RF2 geometry body: name, a u32, textures, rooms, sub-room lists, room links, portals, vertices, faces. With
        /// <paramref name="statistics"/> (the static geometry) the counts are kept and reading stops at the face count.
        /// </summary>
        private void Rf2Geometry(ref RflSpan r, string texturePlace, bool statistics)
        {
            r.Rf2Str();
            r.Skip(4);
            int textures = r.Count(2);
            for (int i = 0; i < textures; i++) Reference(RflReferenceKind.Texture, r.Rf2Str(), texturePlace);
            // RF2 room: id, bounds, four flag-like bytes, life, EAX effect name and 40 bytes; no liquid or ambient blocks
            int rooms = r.Count(78);
            for (int i = 0; i < rooms; i++)
            {
                r.Skip(4 + 24 + 4 + 4);
                r.Rf2Str();
                r.Skip(40);
            }
            int subrooms = r.Count(8);
            for (int i = 0; i < subrooms; i++)
            {
                r.Skip(4);
                r.Skip(4L * r.Count(4));
            }
            r.Skip(8L * r.Count(8)); // room links
            int portals = r.Count(32);
            r.Skip(32L * portals);
            int vertices = r.Count(12);
            r.Skip(12L * vertices);
            int faces = r.Count(55);
            if (statistics)
            {
                _rooms = rooms; _portals = portals; _vertices = vertices; _faces = faces; _geometryTextures = textures;
                return;
            }
            for (int i = 0; i < faces; i++)
            {
                // plane, texture, surface, face id, unknown; flags; smoothing; two scroll periods when flagged 0x8000;
                // emissive colour and intensity; room; corners of vertex, uv and colour
                r.Skip(16 + 16);
                uint flags = r.U32();
                r.Skip(4);
                if ((flags & 0x8000) != 0) r.Skip(8);
                r.Skip(3 + 4 + 4);
                r.Skip(16L * r.Count(16));
            }
        }

        private void Rf2LevelProperties(ref RflSpan r)
        {
            string geomod = r.Rf2Str();
            int hardness = r.I32();
            var ambient = r.Color();
            bool directional = r.Bool();
            var fog = r.Color();
            float near = r.F32(), far = r.F32();
            _props = new RflLevelProperties(geomod, hardness, ambient, directional, fog, near, far);
            Reference(RflReferenceKind.Texture, geomod, "geomod texture");
            _rf2Props = new RflRf2Properties(r.Color(), r.F32(), r.F32(), r.F32(), r.F32(), r.Color(), r.I32(), r.F32());
        }

        private void Rf2Events(ref RflSpan r)
        {
            int n = r.Count(60);
            var classes = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                r.Skip(4);
                string cls = r.Rf2Str();
                r.Skip(12);
                r.Rf2Str();
                // hidden, delay, two i32, two bytes, four i32, two floats, an i32
                r.Skip(1 + 4 + 8 + 2 + 16 + 8 + 4);
                string s1 = r.Rf2Str(), s2 = r.Rf2Str(), s3 = r.Rf2Str();
                r.Skip(4L * r.Count(4));
                if (cls is "Play_Explosion" or "Teleport") r.Skip(36);
                r.Skip(4);
                Tally(classes, cls);
                Rf2EventFile(cls, s1);
                Rf2EventFile(cls, s2);
                Rf2EventFile(cls, s3);
            }
            if (r.Left != 0) throw new RflBodyException($"{r.Left} bytes left after the events");
            _eventClasses = classes;
            AddCount("Events", n);
        }

        // RF2 event classes whose strings name files. Other classes keep stale strings from an earlier class (an Invert or
        // Delay event that still holds a sound name), which the game does not load.
        private static readonly HashSet<string> Rf2FileEventClasses = new(StringComparer.Ordinal)
        {
            "Play_Sound", "Music_Start", "Play_Animation", "Load_Level", "Drip_Generator",
        };

        /// <summary>An RF2 event string that names a file of a known kind.</summary>
        private void Rf2EventFile(string cls, string value)
        {
            if (!Rf2FileEventClasses.Contains(cls)) return;
            int dot = value.LastIndexOf('.');
            if (dot <= 0) return;
            string ext = value[(dot + 1)..];
            var kind = KindOfExtension(ext) ?? (ext.Equals("vcm", StringComparison.OrdinalIgnoreCase) ? RflReferenceKind.Mesh : null);
            if (kind is null) return;
            Reference(kind.Value, value, "event " + cls);
        }

        private void Rf2Decals(ref RflSpan r)
        {
            int n = r.Count(90);
            for (int i = 0; i < n; i++)
            {
                Rf2Head(ref r);
                r.Skip(12);
                Reference(RflReferenceKind.Texture, r.Rf2Str(), "decal");
                r.Skip(4 + 1 + 4 + 4 + 12);
            }
            AddCount("Decals", n);
        }

        private void Rf2PushRegions(ref RflSpan r)
        {
            int n = r.Count(69);
            for (int i = 0; i < n; i++)
            {
                Rf2Head(ref r);
                int shape = r.I32();
                r.Skip(shape is 0 or 1 ? 4 : 12); // a radius, or box extents
                r.Skip(8);
            }
            AddCount("Push regions", n);
        }

        private void Rf2Movers(ref RflSpan r)
        {
            int n = r.Count(60);
            for (int i = 0; i < n; i++)
            {
                r.Skip(4 + 12 + 36);
                Rf2Geometry(ref r, "mover geometry", statistics: false);
                uint flags = r.U32();
                r.Skip(8);
                if ((flags & 0xC) == 0xC) r.Skip(29);
            }
            AddCount("Movers", n);
        }

        private void Rf2MovingGroups(ref RflSpan r)
        {
            int n = r.Count(40), keyframes = 0;
            for (int i = 0; i < n; i++)
            {
                r.Rf2Str();
                r.Skip(1 + 4);
                bool moving = r.Bool();
                // the moving block is stored even for a group that does not move
                int k = r.Count(30);
                for (int j = 0; j < k; j++)
                {
                    r.Skip(52);
                    r.SkipStr();
                    r.Skip(1 + 20 + 12 + 4);
                }
                if (moving) keyframes += k;
                r.Skip(52L * r.Count(52));
                r.Skip(6 + 1 + 8);
                for (int s = 0; s < 4; s++)
                {
                    string sound = r.Rf2Str();
                    if (moving) Reference(RflReferenceKind.Sound, sound, "moving group sound");
                    r.Skip(4);
                }
                r.Skip(4);
                r.Skip(4L * r.Count(4));
                r.Skip(4L * r.Count(4));
                if (r.I32() != -1) r.Skip(48);
                r.Skip(36);
            }
            AddCount("Moving groups", n);
            AddCount("Keyframes", keyframes);
        }

        /// <summary>The RF2 resource lists: names, then (except for bitmaps) one reference count per name; character meshes are name pairs.</summary>
        private void Rf2FileList(uint id, ref RflSpan r)
        {
            int n = r.Count(2);
            var names = new List<string>(n);
            for (int i = 0; i < n; i++)
            {
                names.Add(r.Rf2Str());
                if (id == 0x7001) r.Rf2Str();
            }
            if (id != 0x7000) r.Skip(4L * n);
            _preloads[id] = [.. names];
        }

        private void Rf2Coronas(ref RflSpan r)
        {
            int n = r.Count(72);
            for (int i = 0; i < n; i++)
            {
                Rf2Head(ref r);
                r.Skip(4 + 5); // colour, flags
                string bitmap = r.Rf2Str();
                // a corona without a bitmap stores none of its five glare values
                if (bitmap.Length > 0) r.Skip(20);
                Reference(RflReferenceKind.Texture, bitmap, "corona");
                string volumetric = r.Rf2Str();
                if (volumetric.Length > 0) r.Skip(12);
                Reference(RflReferenceKind.Texture, volumetric, "corona volumetric");
                r.Skip(5);
            }
            AddCount("Coronas", n);
        }

        private void Rf2Items(ref RflSpan r)
        {
            int n = r.Count(63);
            var classes = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                Tally(classes, Rf2Head(ref r));
                r.Skip(12 + 6);
            }
            AddCount("Items", n);
            _itemClasses = classes;
        }

        private void Rf2Clutter(ref RflSpan r)
        {
            int n = r.Count(63);
            var classes = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                Tally(classes, Rf2Head(ref r));
                r.Rf2Str(); // skin
                r.Skip(4L * r.Count(4));
            }
            AddCount("Clutter", n);
            _clutterClasses = classes;
        }

        private void Rf2Triggers(ref RflSpan r)
        {
            int n = r.Count(40);
            for (int i = 0; i < n; i++)
            {
                r.Skip(4);
                r.Rf2Str();
                r.Skip(1);
                int shape = r.I32();
                r.Skip(4 + 4 + 1);
                r.Skip(r.U8()); // the key name has a one-byte length
                r.Skip(1 + 12);
                r.Skip(shape == 0 ? 4 : 36 + 12);
                r.Skip(4 + 4 + 4 + 1 + 4 + 4 + 1 + 2 + 4);
                r.Skip(4L * r.Count(4));
            }
            AddCount("Triggers", n);
        }

        /// <summary>
        /// An object list whose records are known only as far as their object head: the stored count is kept when it fits,
        /// and checked by finding each record's head (an id, a class name, a position and an orthonormal orientation, a
        /// script name and a hidden flag). Entity classes are kept only when every record's head was found.
        /// </summary>
        private string Rf2Scan(uint id, ref RflSpan r)
        {
            string label = Rf2CountLabel(id)!;
            int n = r.I32();
            if (n < 0 || n > r.Left) throw new RflBodyException($"count {n} does not fit in the {r.Left} bytes left");
            var classes = ScanHeads(r.From(r.Position));
            r.Position = r.Length;
            AddCount(label, n);
            if (classes.Count != n) return LayoutNotKnown;
            if (id == 0x30000)
            {
                var tally = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var cls in classes) Tally(tally, cls);
                _entityClasses = tally;
            }
            var distinct = classes.Distinct(StringComparer.Ordinal).ToList();
            return id == 0x30000 ? "count and classes only; layout not known"
                : $"{LayoutNotKnown}; class " + string.Join(", ", distinct.Take(3).Select(c => $"\"{c}\"")) + (distinct.Count > 3 ? ", ..." : "");
        }

        /// <summary>The class names of the object heads found in <paramref name="b"/>, in order.</summary>
        internal static List<string> ScanHeads(ReadOnlySpan<byte> b)
        {
            var found = new List<string>();
            int p = 0;
            while (p + 4 + 2 + 1 + 48 + 2 + 1 <= b.Length)
            {
                if (TryHead(b, p, out string? cls, out int end))
                {
                    found.Add(cls!);
                    p = end;
                }
                else p++;
            }
            return found;
        }

        private static bool TryHead(ReadOnlySpan<byte> b, int p, out string? cls, out int end)
        {
            cls = null;
            end = p;
            int len = BinaryPrimitives.ReadUInt16LittleEndian(b[(p + 4)..]);
            int q = p + 6;
            if (len is < 1 or > 64 || q + len + 48 + 3 > b.Length) return false;
            for (int i = 0; i < len; i++)
            {
                if (b[q + i] is < 0x20 or > 0x7E) return false;
            }
            int text = q;
            q += len;
            Span<float> f = stackalloc float[12];
            for (int i = 0; i < 12; i++)
            {
                f[i] = BinaryPrimitives.ReadSingleLittleEndian(b[(q + 4 * i)..]);
                if (!float.IsFinite(f[i]) || MathF.Abs(f[i]) > 1e7f) return false;
            }
            // the three rows of the orientation: unit length and at right angles to each other
            for (int a = 0; a < 3; a++)
            {
                if (MathF.Abs(Dot(f, a, a) - 1f) > 0.02f) return false;
                for (int c = a + 1; c < 3; c++)
                {
                    if (MathF.Abs(Dot(f, a, c)) > 0.02f) return false;
                }
            }
            q += 48;
            int script = BinaryPrimitives.ReadUInt16LittleEndian(b[q..]);
            if (script == 0xFFFF) script = 0;
            if (script > 256 || q + 2 + script + 1 > b.Length) return false;
            q += 2 + script;
            if (b[q] > 1) return false;
            cls = System.Text.Encoding.Latin1.GetString(b.Slice(text, len));
            end = q + 1;
            return true;

            static float Dot(ReadOnlySpan<float> m, int a, int c) =>
                m[3 + 3 * a] * m[3 + 3 * c] + m[4 + 3 * a] * m[4 + 3 * c] + m[5 + 3 * a] * m[5 + 3 * c];
        }
    }
}
