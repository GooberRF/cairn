using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;

namespace Cairn.Lvl.Writing;

/// <summary>
/// What a write had to change because the target layout cannot hold it, counted by message. Each entry becomes one
/// line of the writer's notes ("3 events ...").
/// </summary>
internal sealed class LvlWriteNotes
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    /// <summary>Counts one occurrence of <paramref name="what"/>, a message that starts with a count placeholder ("{0} events ...").</summary>
    public void Add(string what, int count = 1)
    {
        if (count <= 0) return;
        if (!_counts.TryGetValue(what, out int n)) _order.Add(what);
        _counts[what] = n + count;
    }

    public ImmutableArray<string> Build() =>
        [.. _order.Select(m => string.Format(System.Globalization.CultureInfo.InvariantCulture, m, _counts[m]))];
}

/// <summary>
/// Writes the records levels and group files share (solids, brushes, every object type, moving data, Alpine
/// chunks) with the layout <see cref="LvlRecordReader"/> reads: every field is gated by the target version exactly
/// as the reader gates it, so reading the output with the same rules gives the same model. Fields a version does
/// not store are left out; fields it needs that the model lacks (pre-release legacy values) get the editor's
/// defaults.
/// </summary>
internal sealed class LvlRecordWriter(LvlRules rules, LvlWriter w, LvlWriteNotes notes, bool asStored)
{
    private readonly int _v = rules.Version;
    private readonly LvlRecordReader _gates = new(rules);

    public LvlRules Rules { get; } = rules;

    // ---------- common pieces ----------

    private void Head(RfPlacedObject o)
    {
        w.I32(o.Uid).Str(o.ClassName).Vec3(o.Position).StockMatrix(o.Orientation).Str(o.ScriptName).Bool(o.HiddenInEditor);
    }

    public void List<T>(ImmutableArray<T> items, Action<T> write)
    {
        var list = items.IsDefault ? [] : items;
        w.I32(list.Length);
        foreach (var item in list) write(item);
    }

    private void ClassList(ImmutableArray<RfClassListEntry> list)
    {
        if (_v < 0x34) return;
        List(list, e => w.Str(e.Name).I32(e.Value));
    }

    // ---------- solids ----------

    public void Geometry(RfGeometry g)
    {
        if (_v >= 0xC8)
            w.U32(g.Unknown).U32(g.Modifiability).Str(g.Name);
        else
            w.Str(g.Name).U32(g.Modifiability);

        List(g.Textures, t => w.Str(t));

        var faces = g.Faces.IsDefault ? [] : g.Faces;
        var faceIds = FaceIds(faces);
        var scrolls = new List<RfScrollEntry>();
        for (int i = 0; i < faces.Length; i++)
            if (faces[i].Scroll is { } speed) scrolls.Add(new RfScrollEntry(faceIds[i], speed));
        // Face order, then the entries no face claims. RED's own order is that of its internal face list, which a
        // file does not record, so a list of several entries may come back in another order.
        if (!g.OrphanScrolls.IsDefault) scrolls.AddRange(g.OrphanScrolls);

        if (_v > 0xB4)
        {
            Scrolls(scrolls);
        }
        else
        {
            var legacy = g.LegacyRecords.IsDefault ? [] : g.LegacyRecords;
            int records = legacy.Length / 41;
            w.I32(records).Bytes(legacy.AsSpan(0, records * 41));
        }

        List(g.Rooms, Room);
        if (_v >= 0x71) List(g.SubroomLists, s => w.I32(s.Room).Uids(s.Subrooms));
        List(g.Portals, p => w.I32(p.RoomA).I32(p.RoomB).Vec3(p.Point1).Vec3(p.Point2));
        List(g.Vertices, v => w.Vec3(v));

        var vertices = g.Vertices.IsDefault ? [] : g.Vertices;
        w.I32(faces.Length);
        for (int i = 0; i < faces.Length; i++) Face(faces[i], faceIds[i], vertices);

        List(g.Surfaces, Surface);
        if (_v <= 0xB4 && _v >= 0x5E) Scrolls(scrolls);
        else if (_v < 0x5E) notes.Add("{0} faces lost their texture scrolling (this version stores no scroll list).", scrolls.Count);
    }

    private void Scrolls(List<RfScrollEntry> scrolls)
    {
        w.I32(scrolls.Count);
        foreach (var s in scrolls) w.I32(s.FaceId).Vec2(s.Speed);
    }

    /// <summary>
    /// The face ids to write. The reader gives a scroll entry to the first face with its id, so a scrolling face
    /// needs an id no earlier face has; faces without one get a new id (RED leaves -1 on many brush faces).
    /// </summary>
    private int[] FaceIds(ImmutableArray<RfFace> faces)
    {
        var ids = new int[faces.Length];
        bool anyScroll = false;
        for (int i = 0; i < faces.Length; i++)
        {
            ids[i] = faces[i].FaceId;
            anyScroll |= faces[i].Scroll is not null;
        }
        if (!anyScroll) return ids;
        int next = ids.Length == 0 ? 0 : Math.Max(0, ids.Max() + 1);
        var seen = new HashSet<int>();
        int assigned = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            if (faces[i].Scroll is not null && (ids[i] == -1 || seen.Contains(ids[i])))
            {
                ids[i] = next++;
                assigned++;
            }
            if (ids[i] != -1) seen.Add(ids[i]);
        }
        notes.Add("{0} scrolling faces were given a face id of their own (the scroll list finds faces by id).", assigned);
        return ids;
    }

    private void Room(RfRoom r)
    {
        if (_v >= 0x56) w.I32(r.Id);
        w.Vec3(r.BoundsMin).Vec3(r.BoundsMax);
        if (_v >= 0x4B) w.Bool(r.IsSky);
        if (_v >= 0x65) w.Bool(r.IsCold);
        if (_v >= 0x68) w.Bool(r.IsOutside).Bool(r.IsAirlock);
        bool liquid = _v >= 0x53 && r.Liquid is not null;
        bool ambient = _v >= 0x78 && r.AmbientColor is not null;
        if (_v >= 0x53) w.Bool(liquid);
        if (_v >= 0x78) w.Bool(ambient);
        if (_v >= 0x71) w.Bool(r.IsSubroom);
        if (_v >= 0x75) w.Bool(r.HasAlpha);
        if (_v >= 0x7A) w.F32(r.Life);
        if (_v >= 0xB4) w.Str(r.EaxEffect);
        if (liquid)
        {
            var p = r.Liquid!;
            w.F32(p.Depth).Color(p.Color).Str(p.SurfaceTexture).F32(p.Visibility);
            if (_v >= 0x59) w.I32(p.LiquidType);
            if (_v >= 0x6D) w.I32(p.Alpha);
            if (_v >= 0x74) w.Bool(p.Plankton).I32(p.PixelsPerMeterU).I32(p.PixelsPerMeterV).F32(p.Angle).I32(p.Waveform);
            if (_v >= 0x81) w.Vec2(p.Scroll);
        }
        if (ambient) w.Color(r.AmbientColor!.Value);
    }

    private void Face(RfFace f, int faceId, ImmutableArray<Vector3> vertices)
    {
        if (_v >= 0xA7)
        {
            var plane = f.Plane ?? PlaneOf(f, vertices);
            w.Vec3(plane.Normal).F32(plane.Distance);
        }
        // RF1 and RED keep the low 16 bits; the raw value is written only to reproduce a file as stored.
        uint flags = asStored && (f.RawFlags & 0xFFFF) == (uint)f.Flags ? f.RawFlags : (uint)f.Flags;
        w.I32(f.Texture).I32(f.Surface).I32(faceId).I32(f.ReservedA).I32(f.ReservedB).I32(f.PortalPlus2)
            .U32(flags).U32(f.Smoothing).I32(f.Room);
        var corners = f.Corners.IsDefault ? [] : f.Corners;
        bool lightmap = RfFace.SurfaceHasLightmapUvs(f.Surface);
        w.I32(corners.Length);
        foreach (var c in corners)
        {
            w.I32(c.Vertex).Vec2(c.Uv);
            if (lightmap) w.Vec2(c.LightmapUv ?? Vector2.Zero);
        }
    }

    /// <summary>The plane of a face from its corners (Newell's normal; RF faces wind so that it points out of the solid).</summary>
    public static RfPlane PlaneOf(RfFace f, ImmutableArray<Vector3> vertices)
    {
        double nx = 0, ny = 0, nz = 0;
        var corners = f.Corners.IsDefault ? [] : f.Corners;
        int count = corners.Length;
        var first = Vector3.Zero;
        bool any = false;
        for (int k = 0; k < count; k++)
        {
            int ia = corners[k].Vertex, ib = corners[(k + 1) % count].Vertex;
            if ((uint)ia >= (uint)vertices.Length || (uint)ib >= (uint)vertices.Length) continue;
            var a = vertices[ia];
            var b = vertices[ib];
            if (!any)
            {
                first = a;
                any = true;
            }
            nx += (double)(a.Y - b.Y) * (a.Z + b.Z);
            ny += (double)(a.Z - b.Z) * (a.X + b.X);
            nz += (double)(a.X - b.X) * (a.Y + b.Y);
        }
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (!(len > 1e-20) || !double.IsFinite(len)) return new RfPlane(Vector3.UnitY, -first.Y);
        var n = new Vector3((float)(nx / len), (float)(ny / len), (float)(nz / len));
        return new RfPlane(n, -Vector3.Dot(n, first));
    }

    private void Surface(RfSurface s)
    {
        var legacy = s.LegacyData.IsDefault ? [] : s.LegacyData;
        float Legacy(int i) => i < legacy.Length ? legacy[i] : 0f;
        w.I32(s.Lightmap).U8(s.X).U8(s.Y).U8(s.Width).U8(s.Height);
        if (_v < 0x73)
            for (int i = 0; i < 12; i++) w.F32(Legacy(i));
        w.Vec2(s.PixelsPerMeter).Vec3(s.BoundsMin).Vec3(s.BoundsMax);
        if (_v >= 0x6E) w.Vec3(s.Plane.Normal).F32(s.Plane.Distance).I32(s.ShouldSmooth);
        if (_v >= 0x7B) w.I32(s.Unknown);
        if (_v >= 0x6E) w.I32(s.DroppedAxis).I32(s.UAxis).I32(s.VAxis).Vec2(s.UvAdd).Vec2(s.UvScale);
        if (_v >= 0x6E && _v < 0x73)
        {
            int n = Math.Max(0, legacy.Length - 12) / 8;
            w.I32(n);
            for (int i = 0; i < n * 8; i++) w.F32(Legacy(12 + i));
        }
        if (_v >= 0x73) w.I32(s.Room);
    }

    // ---------- brushes ----------

    public void Brush(RfBrush b)
    {
        if (_v >= 0x57) w.I32(b.Uid);
        w.Vec3(b.Position).StockMatrix(b.Orientation);
        Geometry(b.Geometry);
        if (_v >= 0x51)
        {
            w.U32((uint)b.SolidFlags);
            // Every group file below 0x80 has one more i32 here.
            if (_v < 0x80) w.I32(b.LegacyExtra ?? 0);
        }
        else
        {
            var (a, c) = b.LegacyFlags ?? ((byte)0, (byte)0);
            w.U8(a).U8(c);
        }
        if (_v >= 0x7A) w.I32(b.Life);
        w.I32(b.State);
    }

    // ---------- objects ----------

    public void GeoRegion(RfGeoRegion g)
    {
        w.I32(g.Uid);
        // The hidden flag lives in the packed value.
        uint packed = (g.Packed & ~0x08u) | (g.HiddenInEditor ? 0x08u : 0u);
        w.U32(packed);
        int shape = (int)(packed & 7);
        bool shallow = (packed & 0x20) != 0;
        if (_v < 0xA3 && (packed & 0x10) != 0) w.Str(g.LegacyTexture ?? "");
        if (shallow) w.F32(g.ShallowDepth ?? 0f);
        if (shape == 2)
        {
            w.Vec3(g.Position);
            if (shallow) w.StockMatrix(g.Orientation ?? RfMatrix.Identity);
            if (_v < 0xA3) w.F32(g.LegacyFloat ?? 0f);
            w.F32(g.Radius);
        }
        else if (shape == 4)
        {
            w.Vec3(g.Position).StockMatrix(g.Orientation ?? RfMatrix.Identity).Vec3(g.Size);
            if (_v < 0xA3) w.F32(g.LegacyFloat ?? 0f);
        }
        if (_v < 0xA3) w.I32(g.LegacyInt ?? 0).Color(g.LegacyColor ?? RfColors.White);
    }

    public void Light(RfLight l)
    {
        Head(l);
        if (_v >= 0x62)
        {
            w.U32((uint)l.Flags).Color(l.Color);
        }
        else
        {
            var (a, b) = l.LegacyInts ?? (0, 0);
            w.I32(a).I32(b);
        }
        w.F32(l.Range);
        if (_v < 0x78) w.F32(l.LegacyRangeFloat ?? 0f);
        w.F32(l.Fov).F32(l.FovDropoff);
        if (_v >= 0x78) w.F32(l.IntensityAtMaxRange);
        if (_v >= 0x79) w.I32(l.DropoffType);
        w.F32(l.TubeWidth);
        if (_v >= 0x62)
        {
            w.F32(l.OnIntensity).F32(l.OnTime).F32(l.OnTimeVariation).F32(l.OffIntensity).F32(l.OffTime).F32(l.OffTimeVariation);
        }
        else
        {
            var (a, b) = l.LegacyTailBytes ?? ((byte)0, (byte)0);
            w.F32(l.LegacyTailFloat ?? 0f).Color(l.Color).U8(a).U8(b);
        }
    }

    public void CutsceneCamera(RfCutsceneCamera o) => Head(o);

    public void CutscenePathNode(RfCutscenePathNode o) => Head(o);

    public void Target(RfTarget o) => Head(o);

    public void AmbientSound(RfAmbientSound s)
    {
        w.I32(s.Uid).Vec3(s.Position).Bool(s.HiddenInEditor).Str(s.SoundFile).F32(s.MinDistance).F32(s.Volume);
        if (_v >= 0x67) w.F32(s.Rolloff).I32(s.StartDelayMs);
    }

    public void Event(RfEvent e)
    {
        w.I32(e.Uid).Str(e.ClassName).Vec3(e.Position).Str(e.ScriptName).Bool(e.HiddenInEditor).F32(e.Delay).Bool(e.Bool1);
        if (_v >= 0x64) w.Bool(e.Bool2);
        w.I32(e.Int1).I32(e.Int2).F32(e.Float1).F32(e.Float2).Str(e.Str1).Str(e.Str2).Uids(e.Links);
        // The class decides whether a matrix is stored, never whether the model happens to have one.
        if (_gates.EventHasMatrix(e.ClassName))
        {
            if (e.Orientation is null) notes.Add("{0} events of a class that stores an orientation had none; they were given the identity.");
            w.StockMatrix(e.Orientation ?? RfMatrix.Identity);
        }
        else if (e.Orientation is not null)
        {
            notes.Add("{0} events had an orientation their class does not store in this version; it was left out.");
        }
        if (_v >= 0xB0) w.Color(e.Color);
    }

    public void RespawnPoint(RfRespawnPoint r)
    {
        w.I32(r.Uid).Vec3(r.Position).StockMatrix(r.Orientation).Str(r.ScriptName).Bool(r.HiddenInEditor).I32(r.Team);
        if (_v >= 0xAC) w.Bool(r.RedTeam).Bool(r.BlueTeam).Bool(r.Bot);
    }

    public void ParticleEmitter(RfParticleEmitter p)
    {
        Head(p);
        w.I32(p.Shape).F32(p.SphereRadius).F32(p.PlaneWidth).F32(p.PlaneDepth).Str(p.Texture)
            .F32(p.SpawnDelay).F32(p.SpawnDelayRandom).F32(p.Velocity).F32(p.VelocityRandom).F32(p.Acceleration)
            .F32(p.Decay).F32(p.DecayRandom).F32(p.Radius).F32(p.RadiusRandom).F32(p.GrowthRate).F32(p.GravityScale)
            .F32(p.RandomDirection).Color(p.Color);
        if (_v >= 0x9E) w.Color(p.FadeColor);
        w.U32(p.EmitterFlags).U32(p.ParticleFlags).Bool(p.InitiallyOn);
        if (_v >= 0x77) w.F32(p.TimeOn).F32(p.TimeOnRandom).F32(p.TimeOff).F32(p.TimeOffRandom);
        if (_v >= 0x88) w.F32(p.ActiveDistance);
    }

    public void GasRegion(RfGasRegion g)
    {
        Head(g);
        w.I32(g.Shape);
        if (g.Shape == 1) w.F32(g.Radius);
        if (g.Shape == 2) w.Vec3(g.Size);
        w.Color(g.Color).F32(g.Density);
    }

    public void RoomEffect(RfRoomEffect e)
    {
        int type = _v >= 0x4B ? e.EffectType : 0;
        if (_v >= 0x4B) w.I32(type);
        if (type == 2)
        {
            var p = e.Liquid ?? new RfLiquidProperties();
            w.I32(p.Waveform).F32(p.Depth).Str(p.SurfaceTexture).Color(p.Color).F32(p.Visibility);
            if (_v >= 0x59) w.I32(p.LiquidType);
            if (_v >= 0x6D && _v < 0x81) w.I32(e.LegacyInt ?? 0);
            if (_v >= 0x74) w.Bool(p.Plankton).I32(p.PixelsPerMeterU).I32(p.PixelsPerMeterV).F32(p.Angle);
            if (_v >= 0x81) w.Vec2(p.Scroll);
        }
        if (type == 3 && _v >= 0x78) w.Color(e.AmbientColor ?? RfColors.DefaultAmbient);
        if (_v >= 0x65) w.Bool(e.IsCold);
        if (_v >= 0x68) w.Bool(e.IsOutside).Bool(e.IsAirlock);
        // The object head comes last in this record.
        Head(e);
    }

    public void ClimbingRegion(RfClimbingRegion r)
    {
        Head(r);
        if (_v >= 0x60) w.I32(r.RegionType);
        w.Vec3(r.Extents);
    }

    public void BoltEmitter(RfBoltEmitter b)
    {
        Head(b);
        w.I32(b.TargetUid).F32(b.SourceControlDistance).F32(b.TargetControlDistance).F32(b.Thickness).F32(b.Jitter)
            .I32(b.Segments).F32(b.SpawnDelay).F32(b.SpawnDelayRandom).F32(b.Decay).F32(b.DecayRandom).Color(b.Color)
            .Str(b.Texture).U32(b.Flags).Bool(b.InitiallyOn);
    }

    public void Decal(RfDecal d)
    {
        Head(d);
        w.Vec3(d.Extents).Str(d.Texture);
        if (_v >= 0x5F) w.I32(d.Alpha);
        if (_v >= 0x89) w.Bool(d.SelfIlluminated);
        if (_v >= 0x97) w.I32(d.TilingRaw).F32(d.Scale);
    }

    public void PushRegion(RfPushRegion p)
    {
        Head(p);
        w.I32(p.Shape);
        if (p.Shape == 1) w.F32(p.Radius);
        if (p.Shape is 2 or 3) w.Vec3(p.Extents);
        w.F32(p.Strength).U32(p.Packed);
    }

    public void EaxEffect(RfEaxEffect e)
    {
        if (_v >= 0xB4) w.Str(e.EffectName);
        Head(e);
    }

    public void LegacyWaypointList(RfWaypointList list)
    {
        w.Str(list.Name);
        List(list.LegacyWaypoints, LegacyWaypoint);
    }

    public void LegacyWaypoint(RfLegacyWaypoint p) => w.I32(p.Uid).Vec3(p.Position).Bool(p.HiddenInEditor).F32(p.Value);

    /// <summary>A nav point record (group files store no connection block).</summary>
    public void NavPoint(RfNavPoint n)
    {
        w.I32(n.Uid).Bool(n.HiddenInEditor).F32(n.Height).Vec3(n.Position).F32(n.Radius).I32(n.NavType);
        if (_v >= 0x52 && _v < 0x8B) w.I32(n.LegacyInt ?? 0);
        bool directional = _v >= 0x52 && n.Directional;
        if (_v >= 0x52) w.Bool(directional);
        if (directional && _v >= 0x54) w.StockMatrix(n.Orientation ?? RfMatrix.Identity);
        if (_v >= 0x8A) w.Bool(n.Cover).Bool(n.Hide).Bool(n.Crunch);
        if (_v >= 0x8C) w.F32(n.PauseTime);
        if (_v >= 0x93) w.Uids(n.Links);
    }

    public void Entity(RfEntity e)
    {
        Head(e);
        w.I32(e.Cooperation).I32(e.Friendliness).I32(e.Team).Str(e.WaypointList).Str(e.WaypointMethod).U8(e.Unknown1).Bool(e.Boarded);
        if (_v >= 0x36) w.Bool(e.ReadyToFire);
        if (_v >= 0x40) w.Bool(e.OnlyAttackPlayer);
        if (_v >= 0x46) w.Bool(e.WeaponHolstered);
        if (_v >= 0x48) w.Bool(e.Deaf).I32(e.SweepMin).I32(e.SweepMax);
        if (_v >= 0x4C) w.Bool(e.IgnoreTerrainWhenFiring);
        if (_v >= 0x4C && _v < 0x90) w.I32(e.LegacyInt ?? 0);
        if (_v >= 0x72) w.U8(e.Unknown2);
        if (_v >= 0x74) w.Bool(e.StartCrouched);
        if (_v >= 0x66) w.F32(e.Life);
        if (_v >= 0x9E) w.F32(e.Armor);
        if (_v >= 0x8B) w.I32(e.Fov);
        w.Str(e.PrimaryWeapon).Str(e.SecondaryWeapon).Str(e.ItemDrop).Str(e.StateAnim);
        if (_v >= 0x50) w.Str(e.CorpsePose).Str(e.Skin);
        if (_v >= 0x88) w.Str(e.DeathAnim);
        w.U8(e.AiMode).U8(e.AttackStyle);
        ClassList(e.ClassList);
        if (_v >= 0x62) w.I32(e.TurretUid);
        if (_v >= 0x74) w.I32(e.AlertCameraUid);
        if (_v >= 0x96) w.I32(e.AlarmEventUid);
        if (_v >= 0x7F) w.Bool(e.Run);
        if (_v >= 0x82) w.Bool(e.StartHidden).Bool(e.WearHelmet).Bool(e.EndGameIfKilled).Bool(e.CowerFromWeapon).Bool(e.QuestionUnarmedPlayer);
        if (_v >= 0x9E) w.Bool(e.DontHum).Bool(e.NoShadow);
        if (_v >= 0x9F) w.Bool(e.AlwaysSimulate);
        if (_v >= 0xA1) w.Bool(e.PerfectAim);
        if (_v >= 0xA2) w.Bool(e.PermanentCorpse).Bool(e.NeverFly);
        if (_v >= 0xA4) w.Bool(e.NeverLeave);
        if (_v >= 0xA8) w.Bool(e.NoPersonaMessages);
        if (_v >= 0x83 && _v < 0xA9) w.U8(e.LegacyByte ?? 0);
        if (_v >= 0xA9) w.Bool(e.FadeCorpseImmediately).Bool(e.NeverCollideWithPlayer);
        if (_v >= 0x94)
        {
            w.U8(e.UseCustomAttackRange);
            // The range is stored only when the byte is exactly 1.
            if (e.UseCustomAttackRange == 1) w.F32(e.CustomAttackRange);
        }
        if (_v >= 0x9A) w.Str(e.LeftHandHolding).Str(e.RightHandHolding);
        if (_v >= 0x9D && _v < 0xA9) w.Str(e.LegacyString ?? "");
    }

    public void Item(RfItem i)
    {
        Head(i);
        w.I32(i.Count).I32(i.RespawnTime).I32(i.Team);
    }

    public void Clutter(RfClutter c)
    {
        Head(c);
        ClassList(c.ClassList);
        if (_v >= 0x67 && _v < 0x7C) w.I32(c.LegacySkinIndex ?? 0);
        if (_v >= 0x76) w.Str(c.Skin);
        if (_v >= 0x7D) w.Uids(c.Links);
    }

    public void Trigger(RfTrigger t)
    {
        w.I32(t.Uid).Str(t.ScriptName).Bool(t.HiddenInEditor).I32(t.Shape);
        if (_v >= 0x86) w.F32(t.ResetsAfter);
        else w.I32((int)MathF.Round(float.IsFinite(t.ResetsAfter) ? t.ResetsAfter : 0f));
        w.I32(t.ResetsTimes).Bool(t.UseKey);
        if (_v >= 0x67) w.Str(t.KeyName);
        w.Bool(t.WeaponActivates).U8(t.ActivatedBy).Bool(t.IsNpc).Bool(t.IsAuto);
        if (_v >= 0xA2) w.Bool(t.InVehicle);
        if (t.Shape == 0)
        {
            w.Vec3(t.Position).F32(t.Radius);
        }
        else
        {
            w.Vec3(t.Position).StockMatrix(t.Orientation).Vec3(t.Size);
            if (_v >= 0x6D) w.Bool(t.OneWay);
        }
        if (_v >= 0x68) w.I32(t.AirlockRoomUid);
        if (_v >= 0xA5) w.I32(t.AttachedToUid);
        if (_v >= 0xA6) w.I32(t.UseClutterUid);
        if (_v >= 0x74) w.Bool(t.Disabled);
        if (_v >= 0x77) w.F32(t.ButtonActiveTime);
        if (_v >= 0x7E) w.F32(t.InsideTime);
        if (_v >= 0xB1) w.I32(t.Team);
        w.Uids(t.Links);
    }

    // ---------- groups ----------

    public void Keyframe(RfKeyframe k)
    {
        w.I32(k.Uid).Vec3(k.Position);
        if (_v >= 0x6B) w.StockMatrix(k.Orientation);
        if (_v >= 0x9E) w.Str(k.ScriptName);
        w.Bool(k.HiddenInEditor).F32(k.PauseTime).F32(k.DepartTravelTime);
        if (_v >= 0x5D) w.F32(k.ReturnTravelTime);
        w.F32(k.AccelTime).F32(k.DecelTime);
        if (_v >= 0x5C) w.I32(k.EventUid);
        if (_v >= 0x95) w.I32(k.ItemUid1).I32(k.ItemUid2);
        if (_v >= 0x69 && _v < 0x8E)
        {
            var (a, b) = k.LegacyAngles ?? (Vector3.Zero, Vector3.Zero);
            w.Vec3(a).Vec3(b);
        }
        if (_v >= 0x8E) w.F32(k.DegreesAboutAxis);
    }

    public void MovingData(RfMovingData m)
    {
        List(m.Keyframes, Keyframe);
        List(m.MemberTransforms, t => w.I32(t.Uid).Vec3(t.Position).StockMatrix(t.Orientation));
        w.Bool(m.IsDoor);
        if (_v < 0x68) w.U8(m.LegacyByte ?? 0);
        if (_v >= 0x69) w.Bool(m.RotateInPlace);
        if (_v >= 0x5C) w.Bool(m.StartsBackwards);
        if (_v >= 0x9B) w.Bool(m.UseTravelTimeAsSpeed);
        if (_v >= 0x9C) w.Bool(m.ForceOrient);
        if (_v >= 0xAE && !(Rules.NoPlayerCollideAbsent && _v == 0xAE)) w.Bool(m.NoPlayerCollide);
        w.I32(m.MovementType);
        if (_v >= 0x5C)
        {
            w.I32(m.StartingKeyframe);
            Sound(m.StartSound);
            Sound(m.LoopingSound);
            Sound(m.StopSound);
        }
        if (_v >= 0x67) Sound(m.CloseSound);
    }

    private void Sound(RfSound? s)
    {
        s ??= RfSound.None;
        w.Str(s.File).F32(s.Volume);
    }

    // ---------- Alpine chunks ----------

    /// <summary>How the Alpine mesh records are laid out (the chunk changed between builds without a version).</summary>
    public enum MeshTail
    {
        None,
        Flags,
        FlagsAndSourceWithoutScale,
        Full,
    }

    /// <summary>The Alpine mesh chunk body: a u32 count, the records, then the version 306 trailing blocks.</summary>
    public void AlpineMeshes(IReadOnlyList<RfAlpineMesh> meshes, RfAlpineMeshLayout layout, MeshTail tail)
    {
        w.U32((uint)meshes.Count);
        foreach (var m in meshes)
        {
            w.I32(m.Uid).Vec3(m.Position).AlpineMatrix(m.Orientation).Str(m.ScriptName).Str(m.MeshFile).Str(m.StateAnim).U8(m.Collision);
            var overrides = m.TextureOverrides.IsDefault ? [] : m.TextureOverrides;
            int n = Math.Min(overrides.Length, byte.MaxValue);
            if (n < overrides.Length) notes.Add("{0} Alpine meshes had more than 255 texture overrides; the rest were left out.");
            w.U8((byte)n);
            for (int i = 0; i < n; i++) w.U8(overrides[i].Slot).Str(overrides[i].Texture);
            if (layout == RfAlpineMeshLayout.NoTail) continue;
            if (layout == RfAlpineMeshLayout.ByteMaterial) w.U8((byte)(m.Material ?? 0));
            else w.I32(m.Material ?? 0);
            w.Bool(m.Clutter is not null);
            if (m.Clutter is not { } k) continue;
            w.F32(k.Life).Str(k.Debris).Str(k.ExplosionVclip).F32(k.ExplosionRadius).F32(k.DebrisVelocity);
            var factors = k.DamageFactors.IsDefault ? [] : k.DamageFactors;
            for (int i = 0; i < 11; i++) w.F32(i < factors.Length ? factors[i] : 1f);
            if (layout == RfAlpineMeshLayout.Full)
                w.Str(k.Corpse ?? "").Str(k.CorpseAnim ?? "").U8(k.CorpseCollision ?? 0).I8(k.CorpseMaterial ?? -1);
        }
        if (tail == MeshTail.None) return;
        foreach (var m in meshes) w.U8(m.TrailingFlags ?? 0);
        if (tail == MeshTail.Flags) return;
        foreach (var m in meshes)
        {
            w.U8(m.BrushGeometrySource ?? 0).Str(m.CollisionMesh ?? "");
            if (tail == MeshTail.Full) w.F32(m.DrawScale ?? 1f);
        }
    }

    /// <summary>The Alpine corona chunk body.</summary>
    public void Coronas(IReadOnlyList<RfCorona> coronas)
    {
        w.U32((uint)coronas.Count);
        foreach (var k in coronas)
        {
            w.I32(k.Uid).Vec3(k.Position).AlpineMatrix(k.Orientation).Str(k.ScriptName).Color(k.Color).Str(k.CoronaBitmap)
                .F32(k.ConeAngle).F32(k.Intensity).F32(k.RadiusDistance).F32(k.RadiusScale).F32(k.DiminishDistance)
                .Str(k.VolumetricBitmap);
            if (k.VolumetricBitmap.Length > 0) w.F32(k.VolumetricHeight).F32(k.VolumetricLength);
        }
    }

    /// <summary>The group file's brush metadata chunk body (0x0AFBAE05).</summary>
    public void BrushMetadata(RfBrushMetadata metadata)
    {
        int size = Math.Max(6, metadata.EntrySize);
        var entries = metadata.Entries.IsDefault ? [] : metadata.Entries;
        w.U32(metadata.Version).U16((ushort)Math.Min(size, ushort.MaxValue)).U32((uint)entries.Length);
        foreach (var e in entries)
        {
            w.U32((uint)e.BrushIndex).U8((byte)e.Flags).U8(e.Material);
            var extra = e.ExtraBytes.IsDefault ? [] : e.ExtraBytes;
            for (int i = 0; i < size - 6; i++) w.U8(i < extra.Length ? extra[i] : (byte)0);
        }
    }
}
