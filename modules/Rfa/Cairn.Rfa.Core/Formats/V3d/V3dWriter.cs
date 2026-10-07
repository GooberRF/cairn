namespace Cairn.Rfa.Formats.V3d;

/// <summary>
/// Writes a <see cref="V3dFile"/> back to .v3c / .v3m bytes. Header fields, section size fields,
/// stream sizes and reserved bytes are written exactly as the model holds them; only the LOD data
/// block size is computed (from the layout). Slack inside a stream and alignment padding are
/// zero-filled. A stock file read by <see cref="V3dReader"/> comes back byte for byte.
/// </summary>
public static class V3dWriter
{
    /// <summary>Serialises a mesh.</summary>
    /// <exception cref="ArgumentException">The model is internally inconsistent (counts, sizes or field lengths).</exception>
    public static byte[] Write(V3dFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Sections.IsDefault) throw new ArgumentException("The mesh has no section array.", nameof(file));
        var w = new BinaryBuilder(64 * 1024);
        var h = file.Header;
        w.WriteUInt32(h.Signature);
        w.WriteInt32(h.Version);
        w.WriteInt32(h.SubmeshCount);
        w.WriteInt32(h.TotalVertices);
        w.WriteInt32(h.TotalTriangles);
        w.WriteInt32(h.Unknown0);
        w.WriteInt32(h.TotalMaterials);
        w.WriteInt32(h.Unknown1);
        w.WriteInt32(h.Unknown2);
        w.WriteInt32(h.CollisionSphereCount);

        for (int i = 0; i < file.Sections.Length; i++)
        {
            switch (file.Sections[i])
            {
                case V3dSubmesh s:
                    w.WriteInt32(V3dSectionType.Submesh);
                    w.WriteInt32(s.SizeField);
                    WriteSubmesh(w, s, i);
                    break;
                case V3dCollisionSphere c:
                    CheckName(c.Name, V3dCollisionSphere.NameSize, $"collision sphere (section {i})");
                    w.WriteInt32(V3dSectionType.CollisionSphere);
                    w.WriteInt32(V3dCollisionSphere.Size + Len(c.Extra));
                    w.WriteFixedString(c.Name);
                    w.WriteInt32(c.BoneIndex);
                    w.WriteVector3(c.Position);
                    w.WriteSingle(c.Radius);
                    w.WriteBytes(c.Extra.AsSpan());
                    break;
                case V3dBoneSection b:
                    if (b.Bones.IsDefault) throw new ArgumentException($"Section {i} has no bone array.", nameof(file));
                    w.WriteInt32(V3dSectionType.Bones);
                    w.WriteInt32(4 + b.Bones.Length * V3dBone.Size + Len(b.Extra));
                    w.WriteInt32(b.Bones.Length);
                    foreach (var bone in b.Bones)
                    {
                        CheckName(bone.Name, V3dBone.NameSize, "bone");
                        w.WriteFixedString(bone.Name);
                        w.WriteQuaternion(bone.Rotation);
                        w.WriteVector3(bone.Position);
                        w.WriteInt32(bone.ParentIndex);
                    }
                    w.WriteBytes(b.Extra.AsSpan());
                    break;
                case V3dDumbSection d:
                    w.WriteInt32(V3dSectionType.Dumb);
                    w.WriteInt32(Len(d.Body));
                    w.WriteBytes(d.Body.AsSpan());
                    break;
                case V3dUnknownSection u:
                    if (u.SectionType == V3dSectionType.End)
                        throw new ArgumentException($"Section {i} has the END type code.", nameof(file));
                    w.WriteInt32(u.SectionType);
                    w.WriteInt32(Len(u.Body));
                    w.WriteBytes(u.Body.AsSpan());
                    break;
                default:
                    throw new ArgumentException($"Section {i} is not a section type the writer knows.", nameof(file));
            }
        }

        w.WriteInt32(V3dSectionType.End);
        w.WriteInt32(file.EndSizeField);
        w.WriteBytes(file.TrailingBytes.AsSpan());
        return w.ToArray();
    }

    /// <summary>Writes a mesh to a stream.</summary>
    public static void Write(V3dFile file, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(Write(file));
    }

    private static int Len(System.Collections.Immutable.ImmutableArray<byte> bytes) => bytes.IsDefault ? 0 : bytes.Length;

    private static void WriteSubmesh(BinaryBuilder w, V3dSubmesh s, int index)
    {
        string what = $"submesh {index}";
        CheckName(s.Name, V3dSubmesh.NameSize, what);
        CheckName(s.ParentName, V3dSubmesh.NameSize, what);
        if (s.Lods.IsDefault || s.LodDistances.IsDefault || s.Materials.IsDefault || s.Trailers.IsDefault)
            throw new ArgumentException($"{what} has an uninitialised array.");
        if (s.Lods.Length != s.LodDistances.Length)
            throw new ArgumentException($"{what} has {s.Lods.Length} LODs but {s.LodDistances.Length} LOD distances.");

        w.WriteFixedString(s.Name);
        w.WriteFixedString(s.ParentName);
        w.WriteInt32(s.Version);
        w.WriteInt32(s.Lods.Length);
        foreach (float d in s.LodDistances) w.WriteSingle(d);
        w.WriteVector3(s.Offset);
        w.WriteSingle(s.Radius);
        w.WriteVector3(s.AabbMin);
        w.WriteVector3(s.AabbMax);
        for (int i = 0; i < s.Lods.Length; i++) WriteLod(w, s.Lods[i], $"{what} LOD {i}");

        w.WriteInt32(s.Materials.Length);
        foreach (var m in s.Materials)
        {
            CheckName(m.DiffuseMap, V3dMaterial.NameSize, $"{what} material");
            CheckName(m.ReflectionMap, V3dMaterial.NameSize, $"{what} material");
            w.WriteFixedString(m.DiffuseMap);
            w.WriteSingle(m.Emissive);
            w.WriteSingle(m.Unknown0);
            w.WriteSingle(m.Unknown1);
            w.WriteSingle(m.ReflectionCoefficient);
            w.WriteFixedString(m.ReflectionMap);
            w.WriteUInt32(m.Flags);
        }

        w.WriteInt32(s.Trailers.Length);
        foreach (var t in s.Trailers)
        {
            CheckName(t.Name, V3dSubmesh.NameSize, $"{what} trailing entry");
            w.WriteFixedString(t.Name);
            w.WriteSingle(t.Value);
        }
    }

    private static void WriteLod(BinaryBuilder w, V3dLod lod, string what)
    {
        if (lod.Batches.IsDefault || lod.PropPoints.IsDefault || lod.Textures.IsDefault)
            throw new ArgumentException($"{what} has an uninitialised array.");
        if (lod.Batches.Length > ushort.MaxValue)
            throw new ArgumentException($"{what} has {lod.Batches.Length} batches; at most {ushort.MaxValue} fit.");
        byte[] block = BuildDataBlock(lod, what);

        w.WriteUInt32(lod.Flags);
        w.WriteInt32(lod.VertexCount);
        w.WriteUInt16((ushort)lod.Batches.Length);
        w.WriteInt32(block.Length);
        w.WriteBytes(block);
        w.WriteInt32(lod.Unknown1);
        foreach (var b in lod.Batches)
        {
            w.WriteUInt16((ushort)b.VertexCount);
            w.WriteUInt16((ushort)b.TriangleCount);
            w.WriteUInt16(b.Sizes.PositionsBytes);
            w.WriteUInt16(b.Sizes.TrianglesBytes);
            w.WriteUInt16(b.Sizes.SamePositionOffsetsBytes);
            w.WriteUInt16(b.Sizes.BoneLinksBytes);
            w.WriteUInt16(b.Sizes.TexCoordsBytes);
            w.WriteUInt32(b.RenderFlags);
        }
        w.WriteInt32(lod.PropPoints.Length);
        w.WriteInt32(lod.Textures.Length);
        foreach (var t in lod.Textures)
        {
            if (t.FileName.Contains('\0') || t.FileName.Any(c => c > 0xFF))
                throw new ArgumentException($"{what} texture name '{t.FileName}' cannot be stored.");
            w.WriteByte(t.MaterialIndex);
            w.WriteCString(t.FileName);
        }
    }

    private static byte[] BuildDataBlock(V3dLod lod, string what)
    {
        var w = new BinaryBuilder(16 * 1024);
        foreach (var b in lod.Batches)
        {
            if (b.HeaderReserved0.Length != 0x20 || b.HeaderReserved1.Length != 0x14)
                throw new ArgumentException($"{what} has a batch header of the wrong size.");
            w.WriteBytes(b.HeaderReserved0.AsSpan());
            w.WriteInt32(b.TextureIndex);
            w.WriteBytes(b.HeaderReserved1.AsSpan());
        }
        w.Align(16);

        for (int i = 0; i < lod.Batches.Length; i++)
        {
            var b = lod.Batches[i];
            string bw = $"{what} batch {i}";
            int nv = b.VertexCount, nt = b.TriangleCount;
            if (nv > ushort.MaxValue) throw new ArgumentException($"{bw} has {nv} vertices; at most {ushort.MaxValue} fit.");
            if (nt > ushort.MaxValue) throw new ArgumentException($"{bw} has {nt} triangles; at most {ushort.MaxValue} fit.");
            Expect(b.Normals.Length, nv, $"{bw} normals");
            Expect(b.TexCoords.Length, nv, $"{bw} UVs");
            Expect(b.SamePositionOffsets.Length, nv, $"{bw} same-position offsets");
            Expect(b.BoneLinks.Length, b.Sizes.BoneLinksBytes > 0 ? nv : 0, $"{bw} bone links");
            bool planes = (lod.Flags & V3dLod.FlagTrianglePlanes) != 0;
            Expect(b.Planes.Length, planes ? nt : 0, $"{bw} planes");
            bool morph = (lod.Flags & V3dLod.FlagMorphVerticesMap) != 0;
            Expect(b.MorphMap.Length, morph ? lod.VertexCount : 0, $"{bw} morph map");

            Stream(w, nv * 12, b.Sizes.PositionsBytes, $"{bw} positions", () => { foreach (var p in b.Positions) w.WriteVector3(p); });
            Stream(w, nv * 12, b.Sizes.PositionsBytes, $"{bw} normals", () => { foreach (var n in b.Normals) w.WriteVector3(n); });
            Stream(w, nv * 8, b.Sizes.TexCoordsBytes, $"{bw} UVs", () => { foreach (var uv in b.TexCoords) w.WriteVector2(uv); });
            Stream(w, nt * V3dTriangle.Size, b.Sizes.TrianglesBytes, $"{bw} triangles", () =>
            {
                foreach (var t in b.Triangles)
                {
                    w.WriteUInt16(t.A);
                    w.WriteUInt16(t.B);
                    w.WriteUInt16(t.C);
                    w.WriteUInt16(t.Flags);
                }
            });
            if (planes)
            {
                Stream(w, nt * V3dPlane.Size, nt * V3dPlane.Size, $"{bw} planes", () =>
                {
                    foreach (var p in b.Planes)
                    {
                        w.WriteVector3(p.Normal);
                        w.WriteSingle(p.Distance);
                    }
                });
            }
            Stream(w, nv * 2, b.Sizes.SamePositionOffsetsBytes, $"{bw} same-position offsets",
                () => { foreach (short s in b.SamePositionOffsets) w.WriteInt16(s); });
            if (b.Sizes.BoneLinksBytes > 0)
            {
                Stream(w, nv * V3dBoneLink.Size, b.Sizes.BoneLinksBytes, $"{bw} bone links", () =>
                {
                    foreach (var l in b.BoneLinks)
                    {
                        w.WriteByte(l.Weight0);
                        w.WriteByte(l.Weight1);
                        w.WriteByte(l.Weight2);
                        w.WriteByte(l.Weight3);
                        w.WriteByte(l.Bone0);
                        w.WriteByte(l.Bone1);
                        w.WriteByte(l.Bone2);
                        w.WriteByte(l.Bone3);
                    }
                });
            }
            if (morph)
            {
                Stream(w, lod.VertexCount * 2, lod.VertexCount * 2, $"{bw} morph map",
                    () => { foreach (short m in b.MorphMap) w.WriteInt16(m); });
            }
        }
        w.Align(16);

        foreach (var p in lod.PropPoints)
        {
            CheckName(p.Name, V3dPropPoint.NameSize, $"{what} prop point");
            w.WriteFixedString(p.Name);
            w.WriteQuaternion(p.Rotation);
            w.WriteVector3(p.Position);
            w.WriteInt32(p.ParentIndex);
        }
        return w.ToArray();
    }

    private static void Stream(BinaryBuilder w, int needed, int declared, string what, Action write)
    {
        if (declared < needed)
            throw new ArgumentException($"{what} need {needed} bytes but only {declared} are declared.");
        int start = w.Length;
        write();
        if (w.Length - start != needed) throw new ArgumentException($"{what} do not match their count.");
        w.WriteZeros(declared - needed);
        w.Align(16);
    }

    private static void Expect(int actual, int expected, string what)
    {
        if (actual != expected) throw new ArgumentException($"{what}: {actual} entries, expected {expected}.");
    }

    private static void CheckName(FixedString name, int length, string what)
    {
        if (name.Length != length)
            throw new ArgumentException($"A {what} name field is {name.Length} bytes; it must be {length}.");
    }
}
