using System.Text;
using Cairn.Formats;

namespace Cairn.Vfx.Formats;

/// <summary>
/// Serialises a <see cref="VfxFile"/>. Pure function of (Version, records): derivable header counts are
/// recomputed, every optional field is written iff its version gate is true, and inconsistent models are
/// rejected with <see cref="InvalidOperationException"/>.
/// </summary>
public static class VfxWriter
{
    public static byte[] Write(VfxFile file)
    {
        int v = file.Version;
        if (v < VfxVersion.Minimum) throw new InvalidOperationException($"Version 0x{v:X} is below the minimum 0x{VfxVersion.Minimum:X}.");
        Gate(v, "file", "HeaderFlags", VfxVersion.HasHeaderFlags(v), file.HeaderFlags is not null);
        Gate(v, "file", "LegacyUnk1", VfxVersion.HasHeaderUnk1(v), file.LegacyUnk1 is not null);
        Gate(v, "file", "SelsetObjectCount", VfxVersion.HasSelsets(v), file.SelsetObjectCount is not null);
        var c = VfxHeaderCounts.Compute(file);
        var w = new BinaryBuilder();
        w.WriteUInt32(0x58465356);
        w.WriteInt32(v);
        void G(int? x) { if (x is { } y) w.WriteInt32(y); }
        G(file.HeaderFlags);
        foreach (int x in new[] { file.EndFrame, c.Meshes, c.Lights, c.Dummies, c.ParticleSystems, c.Spacewarps, c.Cameras }) w.WriteInt32(x);
        G(c.Selsets); G(c.Materials); G(c.MixFrames); G(c.SelfIlluminationFrames); G(c.OpacityFrames); G(file.LegacyUnk1);
        foreach (int x in new[] { c.Faces, c.MeshMaterialIndices, c.VertexNormals, c.AdjacentFaces, c.MeshFrames }) w.WriteInt32(x);
        G(c.UvFrames); G(c.MeshTransformFrames); G(c.KeyframeLists); G(c.TranslationKeys); G(c.RotationKeys); G(c.ScaleKeys);
        foreach (int x in new[] { c.LightFrames, c.DummyFrames, c.ParticleFrames, c.SpacewarpFrames, c.CameraFrames }) w.WriteInt32(x);
        G(file.SelsetObjectCount);

        for (int i = 0; i < file.Sections.Length; i++)
        {
            var s = file.Sections[i];
            var b = new BinaryBuilder();
            // Name the object in every validation message, so a refused save tells the user what to fix.
            string? name = s switch { VfxMesh m => m.Name, VfxParticleSystem p => p.Name, VfxDummy d => d.Name, VfxLight l => l.Name, VfxSpacewarp sw => sw.Name, _ => null };
            string ctx = $"section {i} ({s.GetType().Name}{(string.IsNullOrEmpty(name) ? "" : $" '{name}'")})";
            try
            {
            switch (s)
            {
                case VfxMesh m: Mesh(b, m, v, ctx); break;
                case VfxMaterial m:
                    if (!VfxVersion.HasMaterialSections(v)) throw new InvalidOperationException($"{ctx}: material sections need version >= 0x40000.");
                    Material(b, m, v, ctx); break;
                case VfxParticleSystem p: Particles(b, p, v, ctx); break;
                case VfxDummy d:
                    Str(b, d.Name); Str(b, d.Parent); b.WriteByte(d.SaveParent); V3(b, d.Position); Q(b, d.Orientation);
                    b.WriteInt32(d.Frames.Length);
                    foreach (var f in d.Frames) { V3(b, f.Position); Q(b, f.Orientation); }
                    break;
                case VfxLight l:
                    Str(b, l.Name); Str(b, l.Parent); b.WriteByte(l.SaveParent); Light(b, l.Initial);
                    b.WriteInt32(l.Frames.Length);
                    foreach (var f in l.Frames) Light(b, f);
                    break;
                case VfxSpacewarp sw:
                    Str(b, sw.Name); Str(b, sw.Parent); b.WriteInt32(sw.Type); b.WriteInt32(sw.Frames.Length);
                    foreach (var f in sw.Frames)
                    {
                        V3(b, f.Position); Q(b, f.Orientation);
                        foreach (float x in new[] { f.Strength, f.Decay, f.Turbulence, f.Frequency, f.Scale }) b.WriteSingle(x);
                    }
                    break;
                case VfxOpaqueSection o: b.WriteBytes(o.Body.AsSpan()); break;
                default: throw new InvalidOperationException($"{ctx}: unsupported section type.");
            }
            }
            catch (InvalidOperationException ex) when (!ex.Message.StartsWith(ctx, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{ctx}: {ex.Message}", ex);
            }
            byte[] body = b.ToArray();
            w.WriteUInt32(s.Tag);
            w.WriteInt32(body.Length + 4);
            w.WriteBytes(body);
        }
        return w.ToArray();
    }

    private static void Gate(int v, string ctx, string field, bool gate, bool present)
    {
        if (gate != present)
            throw new InvalidOperationException($"{ctx}: {field} must be {(gate ? "present" : "absent")} at version 0x{v:X}.");
    }

    private static void Len(string ctx, string field, int actual, int expected)
    {
        if (actual != expected) throw new InvalidOperationException($"{ctx}: {field} has {actual} entries but {expected} are required.");
    }

    private static void Str(BinaryBuilder b, string s)
    {
        if (s.Contains('\0') || s.Any(ch => ch > 0xFF)) throw new InvalidOperationException($"String '{s}' is not NUL-free Latin-1.");
        b.WriteBytes(Encoding.Latin1.GetBytes(s));
        b.WriteByte(0);
    }

    private static void V2(BinaryBuilder b, Vector2 x) { b.WriteSingle(x.X); b.WriteSingle(x.Y); }
    private static void V3(BinaryBuilder b, Vector3 x) { b.WriteSingle(x.X); b.WriteSingle(x.Y); b.WriteSingle(x.Z); }
    private static void Q(BinaryBuilder b, Quaternion x) { b.WriteSingle(x.X); b.WriteSingle(x.Y); b.WriteSingle(x.Z); b.WriteSingle(x.W); }
    private static void Rgb(BinaryBuilder b, VfxColorI c) { b.WriteInt32(c.R); b.WriteInt32(c.G); b.WriteInt32(c.B); }
    private static void Trs(BinaryBuilder b, VfxTransform t) { V3(b, t.Translation); Q(b, t.Rotation); V3(b, t.Scale); }
    private static void Floats(BinaryBuilder b, ImmutableArray<float> a) { foreach (float x in a) b.WriteSingle(x); }
    private static void Light(BinaryBuilder b, VfxLightParams p)
    {
        V3(b, p.Position); b.WriteSingle(p.Radius); b.WriteSingle(p.Multiplier); V3(b, p.Color); b.WriteByte(p.IsOn);
    }

    private static void Texture(BinaryBuilder b, VfxTexture t, int v, string ctx)
    {
        bool g = VfxVersion.HasTextureAnimation(v);
        Gate(v, ctx, "texture animation fields", g, t.StartFrame is not null && t.PlaybackRate is not null && t.AnimType is not null);
        Str(b, t.Name);
        if (g) { b.WriteInt32(t.StartFrame!.Value); b.WriteSingle(t.PlaybackRate!.Value); b.WriteInt32(t.AnimType!.Value); }
    }

    private static void Mesh(BinaryBuilder b, VfxMesh m, int v, string ctx)
    {
        ctx = $"{ctx} '{m.Name}'";
        int nv = m.NumVertices, nf = m.Faces.Length, frames = m.Frames.Length;
        Gate(v, ctx, "LegacyPositions", VfxVersion.HasLegacyPositions(v), m.LegacyPositions is not null);
        Gate(v, ctx, "Fps", VfxVersion.HasMeshFps(v), m.Fps is not null);
        bool tr = VfxVersion.HasTimeRange(v);
        Gate(v, ctx, "StartTime/EndTime", tr, m.StartTime is not null && m.EndTime is not null);
        Gate(v, ctx, "StartFrame/EndFrame", !tr, m.StartFrame is not null && m.EndFrame is not null);
        Gate(v, ctx, "MaterialIndices", VfxVersion.HasMaterialSections(v), m.MaterialIndices is not null);
        Gate(v, ctx, "InlineMaterials", !VfxVersion.HasMaterialSections(v), m.InlineMaterials is not null);
        Gate(v, ctx, "LegacyFlags", VfxVersion.HasLegacyMeshFlags(v), m.LegacyFlags is not null);
        Gate(v, ctx, "LegacyFacingSize", (m.Flags & VfxMeshFlags.Facing) != 0 && VfxVersion.HasLegacyFacingSize(v), m.LegacyFacingSize is not null);
        Gate(v, ctx, "IsKeyframed", VfxVersion.HasKeyframes(v), m.IsKeyframed is not null);
        bool kf = (m.IsKeyframed ?? 0) != 0;
        Gate(v, ctx, "Pivot", kf && VfxVersion.HasPivot(v), m.Pivot is not null);
        Gate(v, ctx, "Keys", kf, m.Keys is not null);
        if (!tr) Len(ctx, "Frames (from StartFrame/EndFrame)", frames,
            m.EndFrame!.Value - m.StartFrame!.Value + (VfxVersion.HasInclusiveFrameRange(v) ? 1 : 0));

        Str(b, m.Name); Str(b, m.Parent); b.WriteByte(m.SaveParent); b.WriteInt32(nv);
        if (m.LegacyPositions is { } lp) { Len(ctx, "LegacyPositions", lp.Length, nv); foreach (var p in lp) V3(b, p); }
        b.WriteInt32(nf);
        foreach (var f in m.Faces)
        {
            Gate(v, ctx, "face LegacyUvs", VfxVersion.HasLegacyFaceUvs(v), f.LegacyUvs is not null);
            b.WriteInt32(f.V0); b.WriteInt32(f.V1); b.WriteInt32(f.V2);
            if (f.LegacyUvs is { } lu) { Len(ctx, "face LegacyUvs", lu.Length, 3); foreach (var u in lu) V2(b, u); }
            V3(b, f.Color0); V3(b, f.Color1); V3(b, f.Color2); V3(b, f.Normal); V3(b, f.Center); b.WriteSingle(f.Radius);
            b.WriteInt32(f.MaterialIndex); b.WriteInt32(f.SmoothingGroup);
            b.WriteInt32(f.FaceVertex0); b.WriteInt32(f.FaceVertex1); b.WriteInt32(f.FaceVertex2);
        }
        if (m.Fps is { } fps) b.WriteInt32(fps);
        if (tr) { b.WriteSingle(m.StartTime!.Value); b.WriteSingle(m.EndTime!.Value); b.WriteInt32(frames); }
        else { b.WriteInt32(m.StartFrame!.Value); b.WriteInt32(m.EndFrame!.Value); }
        b.WriteInt32(m.MaterialCount);
        if (m.MaterialIndices is { } mi) foreach (int x in mi) b.WriteInt32(x);
        if (m.InlineMaterials is { } ims) foreach (var im in ims) InlineMaterial(b, im, v, frames, ctx);
        V3(b, m.BoundingCenter); b.WriteSingle(m.BoundingRadius);
        if (m.LegacyFlags is { } lf) b.WriteInt32(lf);
        b.WriteUInt32(m.Flags);
        if (m.LegacyFacingSize is { } lfs) V2(b, lfs);
        b.WriteInt32(m.FaceVertices.Length);
        foreach (var fv in m.FaceVertices)
        {
            b.WriteInt32(fv.SmoothingGroup); b.WriteInt32(fv.VertexIndex); b.WriteUInt32(fv.RawU); b.WriteUInt32(fv.RawV);
            b.WriteInt32(fv.AdjacentFaces.Length);
            foreach (int a in fv.AdjacentFaces) b.WriteInt32(a);
        }
        if (m.IsKeyframed is { } k) b.WriteByte(k);
        for (int i = 0; i < frames; i++)
        {
            var f = m.Frames[i];
            var L = VfxMesh.FrameLayout(v, m.Flags, kf, i);
            string fc = $"{ctx} frame {i}";
            Gate(v, fc, "Positions", L.Positions, f.Positions is not null);
            Gate(v, fc, "FacingSize", L.FacingSize, f.FacingSize is not null);
            Gate(v, fc, "UpVector", L.UpVector, f.UpVector is not null);
            Gate(v, fc, "Uvs", L.Uvs, f.Uvs is not null);
            Gate(v, fc, "Transform", L.Transform, f.Transform is not null);
            Gate(v, fc, "LegacyPad", L.Pad, f.LegacyPad is not null);
            Gate(v, fc, "Opacity", L.Opacity, f.Opacity is not null);
            if (f.Positions is { } p)
            {
                Len(fc, "Positions.Raw", p.Raw.Length, 3 * nv);
                V3(b, p.Center); V3(b, p.Multiplier);
                foreach (short x in p.Raw) b.WriteInt16(x);
            }
            if (f.FacingSize is { } fs) V2(b, fs);
            if (f.UpVector is { } up) V3(b, up);
            if (f.Uvs is { } uvs) { Len(fc, "Uvs", uvs.Length, 3 * nf); foreach (var u in uvs) V2(b, u); }
            if (f.Transform is { } t) Trs(b, t);
            if (f.LegacyPad is { } pad) b.WriteByte(pad);
            if (f.Opacity is { } op) b.WriteSingle(op);
        }
        if (m.Pivot is { } pv) Trs(b, pv);
        if (m.Keys is { } keys)
        {
            VKeys(b, keys.Translation);
            b.WriteInt32(keys.Rotation.Length);
            foreach (var r in keys.Rotation)
            {
                b.WriteInt32(r.Time); Q(b, r.Value);
                foreach (float x in new[] { r.Tension, r.Continuity, r.Bias, r.EaseIn, r.EaseOut }) b.WriteSingle(x);
            }
            VKeys(b, keys.Scale);
        }
    }

    private static void VKeys(BinaryBuilder b, ImmutableArray<VfxVectorKey> keys)
    {
        b.WriteInt32(keys.Length);
        foreach (var k in keys) { b.WriteInt32(k.Time); V3(b, k.Value); V3(b, k.InTangent); V3(b, k.OutTangent); }
    }

    private static void InlineMaterial(BinaryBuilder b, VfxInlineMaterial m, int v, int frames, string ctx)
    {
        ctx += " inline material";
        bool tex = m.Type is 0 or 1;
        Gate(v, ctx, "Additive", tex && VfxVersion.HasAdditive(v), m.Additive is not null);
        Gate(v, ctx, "Texture0", tex, m.Texture0 is not null);
        Gate(v, ctx, "Texture1", m.Type == 1, m.Texture1 is not null);
        Gate(v, ctx, "LegacyStartFrame/LegacyAnimType", tex && !VfxVersion.HasTextureAnimation(v), m.LegacyStartFrame is not null && m.LegacyAnimType is not null);
        Gate(v, ctx, "SpecularGlossReflection", tex && VfxVersion.HasSpecular(v), m.SpecularGlossReflection is not null);
        Gate(v, ctx, "ReflectionTexture", tex, m.ReflectionTexture is not null);
        Gate(v, ctx, "Mix", m.Type == 1, m.Mix is not null);
        Gate(v, ctx, "SolidColor", m.Type == 2, m.SolidColor is not null);
        Gate(v, ctx, "SelfIllumination", VfxVersion.HasInlineSelfIllumination(v), m.SelfIllumination is not null);
        b.WriteInt32(m.Type);
        if (m.Additive is { } a) b.WriteByte(a);
        if (m.Texture0 is { } t0) Texture(b, t0, v, ctx);
        if (m.Texture1 is { } t1) Texture(b, t1, v, ctx);
        if (m.LegacyStartFrame is { } lsf) { b.WriteInt32(lsf); b.WriteInt32(m.LegacyAnimType!.Value); }
        if (m.SpecularGlossReflection is { } sgr) V3(b, sgr);
        if (m.ReflectionTexture is { } rt) Str(b, rt);
        if (m.Mix is { } mix) { Len(ctx, "Mix", mix.Length, frames); Floats(b, mix); }
        if (m.SolidColor is { } sc) Rgb(b, sc);
        if (m.SelfIllumination is { } si) b.WriteSingle(si);
    }

    private static void Material(BinaryBuilder b, VfxMaterial m, int v, string ctx)
    {
        bool tex = m.Type is 0 or 1;
        Gate(v, ctx, "Fps", VfxVersion.HasMaterialFps(v), m.Fps is not null);
        Gate(v, ctx, "Additive", tex || VfxVersion.HasAdditiveOnAllMaterials(v), m.Additive is not null);
        Gate(v, ctx, "Texture0", tex, m.Texture0 is not null);
        Gate(v, ctx, "Texture1", m.Type == 1, m.Texture1 is not null);
        Gate(v, ctx, "Mix", m.Type == 1, m.Mix is not null);
        Gate(v, ctx, "LegacyFps", m.Type == 1 && !VfxVersion.HasMaterialFps(v), m.LegacyFps is not null);
        Gate(v, ctx, "SpecularGlossReflection", tex, m.SpecularGlossReflection is not null);
        Gate(v, ctx, "ReflectionTexture", tex, m.ReflectionTexture is not null);
        Gate(v, ctx, "SolidColor", m.Type == 2, m.SolidColor is not null);
        Gate(v, ctx, "Opacity", VfxVersion.HasOpacityFrames(v), m.Opacity is not null);
        if (!VfxVersion.HasMaterialFps(v)) Len(ctx, "SelfIllumination", m.SelfIllumination.Length, 1);
        b.WriteInt32(m.Type);
        if (m.Fps is { } fps) b.WriteInt32(fps);
        if (m.Additive is { } a) b.WriteByte(a);
        if (m.Texture0 is { } t0) Texture(b, t0, v, ctx);
        if (m.Texture1 is { } t1)
        {
            Texture(b, t1, v, ctx);
            b.WriteInt32(m.Mix!.Value.Length);
            if (m.LegacyFps is { } lf) b.WriteInt32(lf);
            Floats(b, m.Mix.Value);
        }
        if (m.SpecularGlossReflection is { } sgr) V3(b, sgr);
        if (m.ReflectionTexture is { } rt) Str(b, rt);
        if (m.SolidColor is { } sc) Rgb(b, sc);
        if (VfxVersion.HasMaterialFps(v)) b.WriteInt32(m.SelfIllumination.Length);
        Floats(b, m.SelfIllumination);
        if (m.Opacity is { } op) { b.WriteInt32(op.Length); Floats(b, op); }
    }

    private static void Particles(BinaryBuilder b, VfxParticleSystem p, int v, string ctx)
    {
        ctx = $"{ctx} '{p.Name}'";
        bool drops = p.IsDrops, fop = VfxVersion.HasParticleFrameOpacity(v);
        Gate(v, ctx, "Flags", VfxVersion.HasParticleFlags(v), p.Flags is not null);
        Gate(v, ctx, "MaterialIndex", VfxVersion.HasMaterialSections(v), p.MaterialIndex is not null);
        Gate(v, ctx, "InlineMaterial", !VfxVersion.HasMaterialSections(v), p.InlineMaterial is not null);
        Gate(v, ctx, "LegacyFlags", !VfxVersion.HasParticleFlags(v), p.LegacyFlags is not null);
        Gate(v, ctx, "Shrink", VfxVersion.HasFloatShrink(v), p.Shrink is not null);
        Gate(v, ctx, "LegacyShrinkBirth/Death", !VfxVersion.HasFloatShrink(v), p.LegacyShrinkBirth is not null && p.LegacyShrinkDeath is not null);
        Gate(v, ctx, "Fade", VfxVersion.HasFade(v), p.Fade is not null);
        Gate(v, ctx, "TailDistance", drops, p.TailDistance is not null);
        Gate(v, ctx, "LegacyBlob", VfxVersion.HasParticleBlob(v), p.LegacyBlob is not null);
        Str(b, p.Name); Str(b, p.Parent); b.WriteByte(p.SaveParent);
        if (p.Flags is { } fl) b.WriteUInt32(fl);
        b.WriteInt32(p.Warps.Length);
        foreach (var wn in p.Warps) Str(b, wn);
        b.WriteInt32(p.StartTime); b.WriteInt32(p.Frames.Length);
        if (p.MaterialIndex is { } mi) b.WriteInt32(mi);
        if (p.InlineMaterial is { } m)
        {
            string mc = ctx + " inline material";
            bool tex = m.Type is 0 or 1;
            Gate(v, mc, "Type", !drops, m.Type is not null);
            Gate(v, mc, "Additive", tex && VfxVersion.HasAdditive(v), m.Additive is not null);
            Gate(v, mc, "Texture0", tex, m.Texture0 is not null);
            Gate(v, mc, "Texture0Rate", tex && VfxVersion.HasTextureAnimation(v), m.Texture0Rate is not null);
            Gate(v, mc, "Texture1", m.Type == 1, m.Texture1 is not null);
            Gate(v, mc, "Texture1Rate", m.Type == 1 && VfxVersion.HasTextureAnimation(v), m.Texture1Rate is not null);
            Gate(v, mc, "Mix", m.Type == 1, m.Mix is not null);
            Gate(v, mc, "DropsColor", drops, m.DropsColor is not null);
            Gate(v, mc, "SelfIllumination", VfxVersion.HasInlineSelfIllumination(v), m.SelfIllumination is not null);
            if (m.Type is { } t) b.WriteInt32(t);
            if (m.Additive is { } a) b.WriteByte(a);
            if (m.Texture0 is { } n0) Str(b, n0);
            if (m.Texture0Rate is { } r0) b.WriteInt32(r0);
            if (m.Texture1 is { } n1) Str(b, n1);
            if (m.Texture1Rate is { } r1) b.WriteInt32(r1);
            if (m.Mix is { } mix) { Len(mc, "Mix", mix.Length, p.Frames.Length); Floats(b, mix); }
            if (m.DropsColor is { } dc) Rgb(b, dc);
            if (m.SelfIllumination is { } si) b.WriteSingle(si);
        }
        b.WriteInt32(p.ParticleCount); b.WriteInt32(p.Start); b.WriteInt32(p.Lifetime); b.WriteSingle(p.LifetimeVariation); b.WriteInt32(p.EmitterType);
        if (p.LegacyFlags is { } lf) b.WriteInt32(lf);
        if (p.Shrink is { } sh) V2(b, sh); else { b.WriteInt32(p.LegacyShrinkBirth!.Value); b.WriteInt32(p.LegacyShrinkDeath!.Value); }
        if (p.Fade is { } fd) V2(b, fd);
        if (p.TailDistance is { } td) b.WriteSingle(td);
        if (p.LegacyBlob is { } blob) { Len(ctx, "LegacyBlob", blob.Length, 56); b.WriteBytes(blob.AsSpan()); }
        foreach (var f in p.Frames)
        {
            Gate(v, ctx, "frame Opacity", fop, f.Opacity is not null);
            V3(b, f.Position); Q(b, f.Orientation);
            foreach (float x in new[] { f.Width, f.Height, f.DropSize, f.Speed, f.SpeedVariation, f.BirthRate }) b.WriteSingle(x);
            if (f.Opacity is { } o) b.WriteSingle(o);
        }
    }
}
