using System.Numerics;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Editing;

public static partial class MeshEdit
{
    /// <summary>
    /// Adds a collision sphere. The CSPH section goes right after the last existing one (or after the
    /// last submesh when there is none, i.e. before the BONE section), and the header's sphere count is
    /// updated.
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="name">Sphere name (24-byte field: at most 23 Latin-1 characters).</param>
    /// <param name="bone">The bone it follows, or -1.</param>
    /// <param name="position">Centre relative to the bone.</param>
    /// <param name="radius">Radius; positive.</param>
    public static V3dFile AddCollisionSphere(V3dFile mesh, string name, int bone, Vector3 position, float radius)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var fixedName = NewName(name, V3dCollisionSphere.NameSize, "collision sphere name", nameof(name));
        CheckBoneOrNone(mesh.Bones.Length, bone, nameof(bone));
        CheckFinite(position, "collision sphere position", nameof(position));
        CheckRadius(radius);

        int insertAt = -1, lastSubmesh = -1;
        for (int i = 0; i < mesh.Sections.Length; i++)
        {
            if (mesh.Sections[i] is V3dCollisionSphere) insertAt = i + 1;
            else if (mesh.Sections[i] is V3dSubmesh) lastSubmesh = i;
        }
        if (insertAt < 0) insertAt = lastSubmesh + 1;
        var sections = mesh.Sections.Insert(insertAt, new V3dCollisionSphere(fixedName, bone, position, radius, []));
        return WithSphereCount(mesh with { Sections = sections });
    }

    /// <summary>Removes a collision sphere and updates the header's sphere count.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="sphere">Index among the CSPH sections, in file order.</param>
    public static V3dFile RemoveCollisionSphere(V3dFile mesh, int sphere)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        int index = SphereSection(mesh, sphere, out _);
        return WithSphereCount(mesh with { Sections = mesh.Sections.RemoveAt(index) });
    }

    /// <summary>
    /// Changes a collision sphere. Extra bytes after its known fields are kept, and an unchanged name
    /// keeps its raw bytes. Values equal to the stored ones are always accepted.
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="sphere">Index among the CSPH sections, in file order.</param>
    /// <param name="name">Sphere name (24-byte field: at most 23 Latin-1 characters).</param>
    /// <param name="bone">The bone it follows, or -1.</param>
    /// <param name="position">Centre relative to the bone.</param>
    /// <param name="radius">Radius; positive.</param>
    public static V3dFile SetCollisionSphere(V3dFile mesh, int sphere, string name, int bone, Vector3 position, float radius)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        int index = SphereSection(mesh, sphere, out var old);
        var fixedName = ResolveName(old.Name, name, V3dCollisionSphere.NameSize, "collision sphere name", nameof(name));
        if (bone != old.BoneIndex) CheckBoneOrNone(mesh.Bones.Length, bone, nameof(bone));
        if (!Same(position, old.Position)) CheckFinite(position, "collision sphere position", nameof(position));
        if (!Same(radius, old.Radius)) CheckRadius(radius);
        if (fixedName == old.Name && bone == old.BoneIndex && Same(position, old.Position) && Same(radius, old.Radius)) return mesh;
        var changed = old with { Name = fixedName, BoneIndex = bone, Position = position, Radius = radius };
        return mesh with { Sections = mesh.Sections.SetItem(index, changed) };
    }

    private static int SphereSection(V3dFile mesh, int sphere, out V3dCollisionSphere value)
    {
        int count = 0;
        for (int i = 0; i < mesh.Sections.Length; i++)
        {
            if (mesh.Sections[i] is not V3dCollisionSphere s) continue;
            if (count == sphere)
            {
                value = s;
                return i;
            }
            count++;
        }
        if (sphere >= 0) count = mesh.CollisionSpheres.Count();
        throw new ArgumentOutOfRangeException(nameof(sphere),
            count == 0 ? $"The mesh has no collision spheres; there is no sphere {sphere}." : $"The mesh has {count} collision spheres (0 to {count - 1}); there is no sphere {sphere}.");
    }

    private static V3dFile WithSphereCount(V3dFile mesh)
    {
        int count = mesh.CollisionSpheres.Count();
        return mesh.Header.CollisionSphereCount == count ? mesh : mesh with { Header = mesh.Header with { CollisionSphereCount = count } };
    }

    private static void CheckRadius(float radius)
    {
        CheckFinite(radius, "collision sphere radius", nameof(radius));
        if (radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius), $"A collision sphere's radius must be greater than 0 (nothing can hit a sphere of radius {radius}).");
    }
}
