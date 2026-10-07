using System.Buffers.Binary;
using System.Text;
using Cairn.Formats;

namespace Cairn.Vfx.Formats;

/// <summary>Bounds-checked little-endian reader over one span; every failure is an <see cref="AssetFormatException"/>.</summary>
internal ref struct VfxSpan
{
    private readonly ReadOnlySpan<byte> _d;
    private readonly string _name;
    public int Pos;
    public readonly int End;

    public VfxSpan(ReadOnlySpan<byte> d, string name, int pos, int end) { _d = d; _name = name; Pos = pos; End = end; }

    public readonly int Remaining => End - Pos;

    public readonly AssetFormatException Fail(string why) => new($"'{_name}' is damaged: {why} (at offset {Pos}).");

    public void Need(long bytes, string what)
    {
        if (bytes < 0 || bytes > Remaining) throw Fail($"{what} needs {bytes} bytes but only {Remaining} remain");
    }

    /// <summary>Validates a count against the bytes left before anything is allocated.</summary>
    public int Count(string what, int minElementSize, int hardLimit = int.MaxValue)
    {
        int n = I32(what);
        if (n < 0) throw Fail($"{what} is negative ({n})");
        if (n > hardLimit) throw Fail($"{what} is implausibly large ({n})");
        if ((long)n * minElementSize > Remaining) throw Fail($"{what} ({n}) exceeds the remaining {Remaining} bytes");
        return n;
    }

    private ReadOnlySpan<byte> Take(int n, string what) { Need(n, what); var s = _d.Slice(Pos, n); Pos += n; return s; }
    public byte U8(string what = "byte") => Take(1, what)[0];
    public short I16(string what = "short") => BinaryPrimitives.ReadInt16LittleEndian(Take(2, what));
    public int I32(string what = "int") => BinaryPrimitives.ReadInt32LittleEndian(Take(4, what));
    public uint U32(string what = "uint") => BinaryPrimitives.ReadUInt32LittleEndian(Take(4, what));
    public float F32(string what = "float") => BinaryPrimitives.ReadSingleLittleEndian(Take(4, what));
    public Vector2 V2(string what = "vec2") { Need(8, what); return new(F32(), F32()); }
    public Vector3 V3(string what = "vec3") { Need(12, what); return new(F32(), F32(), F32()); }
    public Quaternion Q(string what = "quat") { Need(16, what); return new(F32(), F32(), F32(), F32()); }
    public VfxColorI Rgb(string what = "colour") { Need(12, what); return new(I32(), I32(), I32()); }
    public VfxTransform Trs(string what = "transform") { Need(40, what); return new(V3(), Q(), V3()); }
    public ImmutableArray<byte> Bytes(int n, string what) => [.. Take(n, what)];

    public ImmutableArray<float> Floats(int n, string what)
    {
        Need(4L * n, what);
        var b = ImmutableArray.CreateBuilder<float>(n);
        for (int i = 0; i < n; i++) b.Add(F32());
        return b.MoveToImmutable();
    }

    public string Strz(string what = "string")
    {
        int i = _d.Slice(Pos, Remaining).IndexOf((byte)0);
        if (i < 0) throw Fail($"{what} has no terminating NUL");
        string s = Encoding.Latin1.GetString(_d.Slice(Pos, i));
        Pos += i + 1;
        return s;
    }
}

/// <summary>Reads VSFX files into <see cref="VfxFile"/>.</summary>
public static class VfxReader
{
    /// <summary>Upper bound on a frame count whose frames may occupy zero bytes (about 111 minutes at 15 fps).</summary>
    internal const int MaxFrames = 100_000;

    public static VfxFile Read(byte[] data, string name) => Read(data.AsSpan(), name);

    public static VfxFile Read(ReadOnlySpan<byte> data, string name)
    {
        var r = new VfxSpan(data, name, 0, data.Length);
        var (version, flags, endFrame, unk1, selObj, camFrames, stored) = ReadHeader(ref r);
        var sections = ImmutableArray.CreateBuilder<VfxSection>();
        while (r.Pos < r.End)
        {
            uint tag = r.U32("section tag");
            int len = r.I32("section length");
            if (len < 4 || len - 4 > r.Remaining) throw r.Fail($"section length {len} is out of range");
            var s = new VfxSpan(data, name, r.Pos, r.Pos + len - 4);
            VfxSection sec = tag switch
            {
                VfxSectionTag.Mesh => ReadMesh(ref s, version),
                VfxSectionTag.Material when VfxVersion.HasMaterialSections(version) => ReadMaterial(ref s, version),
                VfxSectionTag.ParticleSystem => ReadParticles(ref s, version),
                VfxSectionTag.Dummy => ReadDummy(ref s),
                VfxSectionTag.Light => ReadLight(ref s),
                VfxSectionTag.Spacewarp => ReadSpacewarp(ref s),
                _ => new VfxOpaqueSection(tag, s.Bytes(s.Remaining, "section body")),
            };
            if (s.Remaining != 0) throw s.Fail($"section 0x{tag:X8} has {s.Remaining} unread bytes");
            sections.Add(sec);
            r.Pos = s.End;
        }
        var file = new VfxFile(version, flags, endFrame, unk1, selObj, sections.ToImmutable())
        { CameraFrameCount = camFrames, StoredCounts = stored };
        return file with { Notes = CompareCounts(stored, VfxHeaderCounts.Compute(file)) };
    }

    internal static ImmutableArray<string> CompareCounts(VfxHeaderCounts stored, VfxHeaderCounts computed)
    {
        var notes = ImmutableArray.CreateBuilder<string>();
        foreach (var p in typeof(VfxHeaderCounts).GetProperties())
        {
            if (p.GetIndexParameters().Length != 0 || p.Name == "EqualityContract") continue;
            object? a = p.GetValue(stored), b = p.GetValue(computed);
            if (!Equals(a, b)) notes.Add($"header {p.Name}: stored {a}, recomputed {b}");
        }
        return notes.ToImmutable();
    }

    internal static (int Version, int? Flags, int EndFrame, int? Unk1, int? SelsetObjects, int CameraFrames, VfxHeaderCounts Counts)
        ReadHeader(ref VfxSpan r)
    {
        if (r.Remaining < 8 || r.U32("signature") != 0x58465356) throw r.Fail("not a VSFX file (bad signature)");
        int v = r.I32("version");
        if (v < VfxVersion.Minimum) throw r.Fail($"version 0x{v:X} is older than 0x{VfxVersion.Minimum:X} and is not supported");
        int? flags = Gi(ref r, VfxVersion.HasHeaderFlags(v));
        int endFrame = r.I32(), meshes = r.I32(), lights = r.I32(), dummies = r.I32(), parts = r.I32(), warps = r.I32(), cams = r.I32();
        int? sels = Gi(ref r, VfxVersion.HasSelsets(v)), mats = Gi(ref r, VfxVersion.HasMaterialSections(v)), mix = Gi(ref r, VfxVersion.HasMixFrameTotal(v));
        int? si = Gi(ref r, VfxVersion.HasMaterialFps(v)), op = Gi(ref r, VfxVersion.HasOpacityFrames(v)), unk1 = Gi(ref r, VfxVersion.HasHeaderUnk1(v));
        int faces = r.I32(), matIdx = r.I32(), vn = r.I32(), adj = r.I32(), meshFrames = r.I32();
        int? uvf = Gi(ref r, VfxVersion.HasUvFrames(v));
        bool kf = VfxVersion.HasKeyframes(v);
        int? xf = Gi(ref r, kf), kfl = Gi(ref r, kf), tk = Gi(ref r, kf), rk = Gi(ref r, kf), sk = Gi(ref r, kf);
        int lf = r.I32(), df = r.I32(), pf = r.I32(), wf = r.I32(), cf = r.I32();
        int? selObj = Gi(ref r, VfxVersion.HasSelsets(v));
        var counts = new VfxHeaderCounts(meshes, lights, dummies, parts, warps, cams, sels, mats, mix, si, op,
            faces, matIdx, vn, adj, meshFrames, uvf, xf, kfl, tk, rk, sk, lf, df, pf, wf, cf);
        return (v, flags, endFrame, unk1, selObj, cf, counts);
    }

    private static int? Gi(ref VfxSpan r, bool gate) => gate ? r.I32("header") : null;

    private static VfxTexture Texture(ref VfxSpan r, int v) =>
        VfxVersion.HasTextureAnimation(v) ? new(r.Strz("texture"), r.I32(), r.F32(), r.I32()) : new(r.Strz("texture"), null, null, null);

    private static VfxMesh ReadMesh(ref VfxSpan r, int v)
    {
        string name = r.Strz("mesh name"), parent = r.Strz("mesh parent");
        byte saveParent = r.U8();
        int nv = r.Count("vertex count", VfxVersion.HasLegacyPositions(v) ? 12 : 0, 32767 * 3);
        ImmutableArray<Vector3>? legacy = null;
        if (VfxVersion.HasLegacyPositions(v))
        {
            var b = ImmutableArray.CreateBuilder<Vector3>(nv);
            for (int i = 0; i < nv; i++) b.Add(r.V3());
            legacy = b.MoveToImmutable();
        }
        bool legacyUv = VfxVersion.HasLegacyFaceUvs(v);
        int nf = r.Count("face count", legacyUv ? 100 : 76);
        var faces = ImmutableArray.CreateBuilder<VfxFace>(nf);
        for (int i = 0; i < nf; i++)
        {
            int a = r.I32(), b = r.I32(), c = r.I32();
            ImmutableArray<Vector2>? luv = legacyUv ? ImmutableArray.Create(r.V2(), r.V2(), r.V2()) : null;
            faces.Add(new VfxFace(a, b, c, luv, r.V3(), r.V3(), r.V3(), r.V3(), r.V3(), r.F32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32()));
        }
        int? fps = VfxVersion.HasMeshFps(v) ? r.I32() : null;
        float? st = null, et = null; int? sf = null, ef = null; int frames;
        if (VfxVersion.HasTimeRange(v)) { st = r.F32(); et = r.F32(); frames = r.Count("frame count", 0, MaxFrames); }
        else
        {
            sf = r.I32(); ef = r.I32();
            long n = (long)ef.Value - sf.Value + (VfxVersion.HasInclusiveFrameRange(v) ? 1 : 0);
            if (n < 0 || n > MaxFrames) throw r.Fail($"frame range {sf}..{ef} is invalid");
            frames = (int)n;
        }
        ImmutableArray<int>? matIdx = null; ImmutableArray<VfxInlineMaterial>? inline = null;
        if (VfxVersion.HasMaterialSections(v))
        {
            int nm = r.Count("material count", 4);
            var b = ImmutableArray.CreateBuilder<int>(nm);
            for (int i = 0; i < nm; i++) b.Add(r.I32());
            matIdx = b.MoveToImmutable();
        }
        else
        {
            int nm = r.Count("material count", 4);
            var b = ImmutableArray.CreateBuilder<VfxInlineMaterial>(nm);
            for (int i = 0; i < nm; i++) b.Add(ReadInlineMaterial(ref r, v, frames));
            inline = b.MoveToImmutable();
        }
        var bc = r.V3(); float br = r.F32();
        int? lflags = VfxVersion.HasLegacyMeshFlags(v) ? r.I32() : null;
        uint flags = r.U32("mesh flags");
        Vector2? lfs = (flags & VfxMeshFlags.Facing) != 0 && VfxVersion.HasLegacyFacingSize(v) ? r.V2() : null;
        int nfv = r.Count("face vertex count", 20);
        var fvs = ImmutableArray.CreateBuilder<VfxFaceVertex>(nfv);
        for (int i = 0; i < nfv; i++)
        {
            int sg = r.I32(), vi = r.I32(); uint u = r.U32(), vv = r.U32();
            int na = r.Count("adjacent face count", 4);
            var adj = ImmutableArray.CreateBuilder<int>(na);
            for (int k = 0; k < na; k++) adj.Add(r.I32());
            fvs.Add(new VfxFaceVertex(sg, vi, u, vv, adj.MoveToImmutable()));
        }
        byte? kfb = VfxVersion.HasKeyframes(v) ? r.U8() : null;
        bool kf = (kfb ?? 0) != 0;
        if (frames > 1)
        {
            // Every frame after the first has the same layout: reject a frame count the bytes left cannot hold
            // before looping or allocating (a damaged count otherwise costs a long loop before the failure).
            var L1 = VfxMesh.FrameLayout(v, flags, kf, 1);
            long per = (L1.Positions ? 24 + 6L * nv : 0) + (L1.FacingSize ? 8 : 0) + (L1.UpVector ? 12 : 0)
                + (L1.Uvs ? 24L * nf : 0) + (L1.Pad ? 1 : 0) + (L1.Opacity ? 4 : 0);
            if (per * (frames - 1) > r.Remaining) throw r.Fail($"frame count ({frames}) x {per} bytes exceeds the remaining {r.Remaining} bytes");
        }
        var fr = ImmutableArray.CreateBuilder<VfxMeshFrame>(frames);
        for (int i = 0; i < frames; i++)
        {
            var L = VfxMesh.FrameLayout(v, flags, kf, i);
            VfxCompressedPositions? pos = null;
            if (L.Positions)
            {
                var c = r.V3(); var m = r.V3();
                r.Need(6L * nv, "compressed positions");
                var raw = ImmutableArray.CreateBuilder<short>(3 * nv);
                for (int k = 0; k < 3 * nv; k++) raw.Add(r.I16());
                pos = new(c, m, raw.MoveToImmutable());
            }
            Vector2? fs = L.FacingSize ? r.V2() : null;
            Vector3? up = L.UpVector ? r.V3() : null;
            ImmutableArray<Vector2>? uvs = null;
            if (L.Uvs)
            {
                r.Need(24L * nf, "frame uvs");
                var ub = ImmutableArray.CreateBuilder<Vector2>(3 * nf);
                for (int k = 0; k < 3 * nf; k++) ub.Add(r.V2());
                uvs = ub.MoveToImmutable();
            }
            VfxTransform? trs = L.Transform ? r.Trs() : null;
            byte? pad = L.Pad ? r.U8() : null;
            float? op = L.Opacity ? r.F32() : null;
            var f = new VfxMeshFrame(pos, fs, up, uvs, trs, pad, op);
            fr.Add(f == VfxMeshFrame.Empty ? VfxMeshFrame.Empty : f);
        }
        VfxTransform? pivot = kf && VfxVersion.HasPivot(v) ? r.Trs("pivot") : null;
        VfxKeyLists? keys = null;
        if (kf)
        {
            var t = VKeys(ref r, "translation keys");
            int nr = r.Count("rotation key count", 40);
            var rb = ImmutableArray.CreateBuilder<VfxRotationKey>(nr);
            for (int i = 0; i < nr; i++) rb.Add(new(r.I32(), r.Q(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32()));
            keys = new VfxKeyLists(t, rb.MoveToImmutable(), VKeys(ref r, "scale keys"));
        }
        return new VfxMesh(name, parent, saveParent, nv, legacy, faces.MoveToImmutable(), fps, st, et, sf, ef, matIdx, inline,
            bc, br, lflags, flags, lfs, fvs.MoveToImmutable(), kfb, fr.MoveToImmutable(), pivot, keys);
    }

    private static ImmutableArray<VfxVectorKey> VKeys(ref VfxSpan r, string what)
    {
        int n = r.Count(what, 40);
        var b = ImmutableArray.CreateBuilder<VfxVectorKey>(n);
        for (int i = 0; i < n; i++) b.Add(new(r.I32(), r.V3(), r.V3(), r.V3()));
        return b.MoveToImmutable();
    }

    private static VfxInlineMaterial ReadInlineMaterial(ref VfxSpan r, int v, int frames)
    {
        int t = r.I32("material type");
        bool tex = t is 0 or 1;
        byte? add = null; VfxTexture? t0 = null, t1 = null; int? lsf = null, lat = null; Vector3? sgr = null; string? refl = null;
        ImmutableArray<float>? mix = null; VfxColorI? solid = null;
        if (tex)
        {
            if (VfxVersion.HasAdditive(v)) add = r.U8();
            t0 = Texture(ref r, v);
            if (t == 1) t1 = Texture(ref r, v);
            if (!VfxVersion.HasTextureAnimation(v)) { lsf = r.I32(); lat = r.I32(); }
            if (VfxVersion.HasSpecular(v)) sgr = r.V3();
            refl = r.Strz("reflection texture");
            if (t == 1) mix = r.Floats(frames, "mix frames");
        }
        else if (t == 2) solid = r.Rgb();
        float? si = VfxVersion.HasInlineSelfIllumination(v) ? r.F32() : null;
        return new VfxInlineMaterial(t, add, t0, t1, lsf, lat, sgr, refl, mix, solid, si);
    }

    private static VfxMaterial ReadMaterial(ref VfxSpan r, int v)
    {
        int t = r.I32("material type");
        bool tex = t is 0 or 1;
        int? fps = VfxVersion.HasMaterialFps(v) ? r.I32() : null;
        byte? add = tex || VfxVersion.HasAdditiveOnAllMaterials(v) ? r.U8() : null;
        VfxTexture? t0 = tex ? Texture(ref r, v) : null, t1 = null;
        int? lfps = null; ImmutableArray<float>? mix = null;
        if (t == 1)
        {
            t1 = Texture(ref r, v);
            int n = r.Count("mix count", 4);
            if (!VfxVersion.HasMaterialFps(v)) lfps = r.I32();
            mix = r.Floats(n, "mix frames");
        }
        Vector3? sgr = tex ? r.V3() : null;
        string? refl = tex ? r.Strz("reflection texture") : null;
        VfxColorI? solid = t == 2 ? r.Rgb() : null;
        int nsi = VfxVersion.HasMaterialFps(v) ? r.Count("self illumination count", 4) : 1;
        var si = r.Floats(nsi, "self illumination");
        ImmutableArray<float>? op = VfxVersion.HasOpacityFrames(v) ? r.Floats(r.Count("opacity count", 4), "opacity") : null;
        return new VfxMaterial(t, fps, add, t0, t1, lfps, mix, sgr, refl, solid, si, op);
    }

    private static VfxParticleSystem ReadParticles(ref VfxSpan r, int v)
    {
        string name = r.Strz("particle name"), parent = r.Strz("particle parent");
        byte sp = r.U8();
        uint? flags = VfxVersion.HasParticleFlags(v) ? r.U32() : null;
        bool drops = ((flags ?? 0) & 0x100) != 0;
        int nw = r.Count("warp count", 1);
        var warps = ImmutableArray.CreateBuilder<string>(nw);
        for (int i = 0; i < nw; i++) warps.Add(r.Strz("warp name"));
        int startTime = r.I32();
        int nfr = r.Count("particle frame count", 52);
        int? matIdx = null; VfxParticleMaterial? pm = null;
        if (VfxVersion.HasMaterialSections(v)) matIdx = r.I32();
        else
        {
            int? t = drops ? null : r.I32("particle material type");
            byte? add = null; string? n0 = null, n1 = null; int? r0 = null, r1 = null; ImmutableArray<float>? mix = null;
            if (t is 0 or 1)
            {
                if (VfxVersion.HasAdditive(v)) add = r.U8();
                n0 = r.Strz("texture");
                if (VfxVersion.HasTextureAnimation(v)) r0 = r.I32();
                if (t == 1)
                {
                    n1 = r.Strz("texture");
                    if (VfxVersion.HasTextureAnimation(v)) r1 = r.I32();
                    mix = r.Floats(nfr, "mix frames");
                }
            }
            VfxColorI? dc = drops ? r.Rgb() : null;
            float? si = VfxVersion.HasInlineSelfIllumination(v) ? r.F32() : null;
            pm = new VfxParticleMaterial(t, add, n0, r0, n1, r1, mix, dc, si);
        }
        int count = r.I32(), start = r.I32(), life = r.I32(); float lv = r.F32(); int emitter = r.I32();
        int? lflags = VfxVersion.HasParticleFlags(v) ? null : r.I32();
        Vector2? shrink = null; int? lsb = null, lsd = null;
        if (VfxVersion.HasFloatShrink(v)) shrink = r.V2(); else { lsb = r.I32(); lsd = r.I32(); }
        Vector2? fade = VfxVersion.HasFade(v) ? r.V2() : null;
        float? tail = drops ? r.F32() : null;
        ImmutableArray<byte>? blob = VfxVersion.HasParticleBlob(v) ? r.Bytes(56, "legacy particle block") : null;
        bool fop = VfxVersion.HasParticleFrameOpacity(v);
        r.Need((long)nfr * (fop ? 56 : 52), "particle frames");
        var fr = ImmutableArray.CreateBuilder<VfxParticleFrame>(nfr);
        for (int i = 0; i < nfr; i++)
            fr.Add(new(r.V3(), r.Q(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), fop ? r.F32() : null));
        return new VfxParticleSystem(name, parent, sp, flags, warps.MoveToImmutable(), startTime, matIdx, pm, count, start, life, lv,
            emitter, lflags, shrink, lsb, lsd, fade, tail, blob, fr.MoveToImmutable());
    }

    private static VfxDummy ReadDummy(ref VfxSpan r)
    {
        string name = r.Strz("dummy name"), parent = r.Strz("dummy parent");
        byte sp = r.U8(); var pos = r.V3(); var q = r.Q();
        int n = r.Count("dummy frame count", 28);
        var b = ImmutableArray.CreateBuilder<VfxDummyFrame>(n);
        for (int i = 0; i < n; i++) b.Add(new(r.V3(), r.Q()));
        return new VfxDummy(name, parent, sp, pos, q, b.MoveToImmutable());
    }

    private static VfxLightParams LightParams(ref VfxSpan r) => new(r.V3(), r.F32(), r.F32(), r.V3(), r.U8());

    private static VfxLight ReadLight(ref VfxSpan r)
    {
        string name = r.Strz("light name"), parent = r.Strz("light parent");
        byte sp = r.U8(); var init = LightParams(ref r);
        int n = r.Count("light frame count", 33);
        var b = ImmutableArray.CreateBuilder<VfxLightParams>(n);
        for (int i = 0; i < n; i++) b.Add(LightParams(ref r));
        return new VfxLight(name, parent, sp, init, b.MoveToImmutable());
    }

    private static VfxSpacewarp ReadSpacewarp(ref VfxSpan r)
    {
        string name = r.Strz("spacewarp name"), parent = r.Strz("spacewarp parent");
        int type = r.I32();
        int n = r.Count("spacewarp frame count", 48);
        var b = ImmutableArray.CreateBuilder<VfxSpacewarpFrame>(n);
        for (int i = 0; i < n; i++) b.Add(new(r.V3(), r.Q(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32()));
        return new VfxSpacewarp(name, parent, type, b.MoveToImmutable());
    }
}

/// <summary>Cheap header facts for asset lists, without decoding sections.</summary>
public sealed record VfxProbe(int Version, int EndFrame, VfxHeaderCounts Counts, bool IsEngineFatalVersion)
{
    public static VfxProbe Read(ReadOnlySpan<byte> data, string name)
    {
        var r = new VfxSpan(data, name, 0, data.Length);
        var h = VfxReader.ReadHeader(ref r);
        return new VfxProbe(h.Version, h.EndFrame, h.Counts, VfxVersion.IsEngineFatal(h.Version));
    }
}
