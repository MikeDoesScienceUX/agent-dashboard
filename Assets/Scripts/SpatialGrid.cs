using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 2D spatial hash grid for O(1) insertion and O(k) neighbor queries.
/// Used by AgentController to replace the O(n²) brute-force SFM neighbor loop.
///
/// CrowdManager owns one instance, rebuilds it each FixedUpdate, and exposes it
/// via CrowdManager.Grid so AgentControllers can query neighbours cheaply.
/// </summary>
public class SpatialGrid
{
    private readonly float _cellSize;
    private readonly Dictionary<long, List<AgentController>> _cells
        = new Dictionary<long, List<AgentController>>();

    // Reusable result buffer to avoid allocations in GetNeighbors
    private readonly List<AgentController> _queryBuffer = new List<AgentController>(32);

    public SpatialGrid(float cellSize)
    {
        _cellSize = Mathf.Max(cellSize, 0.1f);
    }

    // ── Write ───────────────────────────────────────────────────────

    /// <summary>Remove all agents from every cell (keeps buckets allocated to avoid GC).</summary>
    public void Clear()
    {
        foreach (var list in _cells.Values)
            list.Clear();
    }

    /// <summary>Insert an agent into the grid cell covering its current position.</summary>
    public void Insert(AgentController agent)
    {
        long key = HashPos(agent.transform.position);
        if (!_cells.TryGetValue(key, out var list))
        {
            list = new List<AgentController>(8);
            _cells[key] = list;
        }
        list.Add(agent);
    }

    // ── Read ────────────────────────────────────────────────────────

    /// <summary>
    /// Fill <paramref name="results"/> with all agents whose cell is within
    /// <paramref name="radius"/> metres of <paramref name="pos"/>.
    /// Does NOT do exact distance filtering — callers do that themselves.
    /// </summary>
    public void GetNeighbors(Vector3 pos, float radius, List<AgentController> results)
    {
        results.Clear();
        int cr = Mathf.CeilToInt(radius / _cellSize);
        int cx = CellCoord(pos.x);
        int cz = CellCoord(pos.z);

        for (int dx = -cr; dx <= cr; dx++)
        for (int dz = -cr; dz <= cr; dz++)
        {
            long key = Hash(cx + dx, cz + dz);
            if (_cells.TryGetValue(key, out var list))
                results.AddRange(list);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private int   CellCoord(float v) => Mathf.FloorToInt(v / _cellSize);
    private long  HashPos(Vector3 p) => Hash(CellCoord(p.x), CellCoord(p.z));

    // Cantor-style bijective hash — safe for negative coords in range ±32767
    private static long Hash(int x, int z)
        => ((long)(x + 32768)) * 65536L + (z + 32768);
}
