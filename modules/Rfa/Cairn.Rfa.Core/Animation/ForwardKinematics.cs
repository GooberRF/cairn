using Cairn.Formats.Maths;

namespace Cairn.Rfa.Animation;

/// <summary>
/// Bone locals to model-space worlds, as the engine's pose build does (0x0051B500):
/// <c>W(root) = L(root)</c>; otherwise <c>W_rot = W_rot(p) * L_rot</c>,
/// <c>W_pos = W_pos(p) + Rotate(W_rot(p), L_pos)</c>. Parents may come after their children in index
/// order (stock rigs do this), so worlds are computed in an evaluation order that puts every parent
/// first. A parent index that is out of range, points at the bone itself or closes a cycle is treated
/// as "no parent" rather than crashing on a damaged mesh.
/// </summary>
public static class ForwardKinematics
{
    /// <summary>
    /// The parents the solver actually uses: <paramref name="parents"/> with every invalid entry
    /// (out of range, self, part of a cycle) replaced by -1.
    /// </summary>
    public static int[] EffectiveParents(ReadOnlySpan<int> parents)
    {
        int n = parents.Length;
        var result = new int[n];
        for (int i = 0; i < n; i++) result[i] = parents[i] >= 0 && parents[i] < n && parents[i] != i ? parents[i] : -1;

        // Break cycles: walk up from each bone; a walk longer than n has looped.
        var state = new byte[n]; // 0 unvisited, 1 on the current path, 2 done
        var path = new List<int>();
        for (int start = 0; start < n; start++)
        {
            if (state[start] != 0) continue;
            path.Clear();
            int i = start;
            while (i >= 0 && state[i] == 0)
            {
                state[i] = 1;
                path.Add(i);
                i = result[i];
            }
            if (i >= 0 && state[i] == 1) result[i] = -1; // the bone that closed the loop becomes a root
            foreach (int p in path) state[p] = 2;
        }
        return result;
    }

    /// <summary>An order in which every bone comes after its (effective) parent.</summary>
    /// <param name="parents">Parent indices, already passed through <see cref="EffectiveParents"/>.</param>
    public static int[] EvaluationOrder(ReadOnlySpan<int> parents)
    {
        int n = parents.Length;
        var order = new int[n];
        var done = new bool[n];
        int count = 0;
        var stack = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            int b = i;
            while (b >= 0 && !done[b])
            {
                stack.Push(b);
                int p = parents[b];
                b = p >= 0 && p < n ? p : -1;
                if (stack.Count > n) break; // defensive: a cycle that slipped through
            }
            while (stack.Count > 0)
            {
                int x = stack.Pop();
                if (done[x]) continue;
                done[x] = true;
                order[count++] = x;
            }
        }
        return order;
    }

    /// <summary>
    /// Computes worlds from locals using a skeleton's cached parents and order. Allocation-free.
    /// </summary>
    /// <param name="skeleton">Supplies the effective parents and evaluation order.</param>
    /// <param name="local">One local per bone.</param>
    /// <param name="world">Receives one world per bone.</param>
    public static void Solve(Skeleton skeleton, ReadOnlySpan<Rigid> local, Span<Rigid> world)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        Solve(skeleton.EffectiveParents.AsSpan(), skeleton.EvaluationOrder.AsSpan(), local, world);
    }

    /// <summary>Computes worlds from locals for arbitrary (possibly invalid) parent indices.</summary>
    public static void Solve(ReadOnlySpan<int> parents, ReadOnlySpan<Rigid> local, Span<Rigid> world)
    {
        var effective = EffectiveParents(parents);
        Solve(effective, EvaluationOrder(effective), local, world);
    }

    /// <summary>Computes worlds from locals given effective parents and a parents-first order.</summary>
    public static void Solve(ReadOnlySpan<int> effectiveParents, ReadOnlySpan<int> order, ReadOnlySpan<Rigid> local, Span<Rigid> world)
    {
        int n = effectiveParents.Length;
        if (local.Length < n || world.Length < n || order.Length < n)
            throw new ArgumentException("Every bone needs a local, a world slot and a place in the order.");
        foreach (int i in order)
        {
            int p = effectiveParents[i];
            world[i] = p < 0 ? local[i] : world[p].Compose(local[i]);
        }
    }
}
