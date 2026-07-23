using System.Numerics;
using System.Text.RegularExpressions;
using RaxicoreEditor.EngineAssets.Meshes;

/// <summary>
/// Rasterizes a continent's per-tile terrain mesh into an <c>N x N</c> water mask.
///
/// A continent's <c>mapNN.ubr</c> holds two relevant kinds of CMeshSystem: terrain tiles named
/// <c>mapNN&lt;CC&gt;&lt;RR&gt;</c> (each 256x256 world units, placed at <c>WorldOffset =
/// (CC*256, RR*256)</c> with tile-local vertex XY in <c>[0..256]</c> and a real Z height), and flat
/// ocean planes named <c>mapNN_oc&lt;CC&gt;&lt;RR&gt;</c> sitting at a single sea-level Z. We take
/// that ocean-plane Z as sea level, rasterize every terrain triangle into a height grid keeping the
/// MAX terrain Z per cell, then mark a cell as water when its highest terrain is still below sea
/// level (or no terrain covers it at all -- open ocean).
/// </summary>
internal sealed class ContinentTerrain
{
    public int N { get; private init; }
    public int WorldSize { get; private init; }
    public float SeaLevel { get; private init; }
    public int TileCount { get; private init; }
    public int WaterCells { get; private init; }
    public byte[] PackedMask { get; private init; } = Array.Empty<byte>();

    private static readonly Regex TileName = new(@"^map\d{2}(\d{2})(\d{2})$", RegexOptions.IgnoreCase);
    private static readonly Regex OceanName = new(@"^map\d{2}_oc\d{2}\d{2}$", RegexOptions.IgnoreCase);

    public static ContinentTerrain Build(string ubrPath, int n)
    {
        var model = UberModel.Load(File.ReadAllBytes(ubrPath));

        // Pass 1: sea level (the flat ocean-plane Z) + world extent (max tile corner + one tile).
        float sea = float.NaN;
        int maxCorner = 0;
        var tileIdx = new List<int>();
        for (int i = 0; i < model.Records.Count; i++)
        {
            string name = model.Records[i].Name;
            if (TileName.IsMatch(name))
            {
                tileIdx.Add(i);
            }
            else if (float.IsNaN(sea) && OceanName.IsMatch(name))
            {
                var oc = model.FetchMeshSystemAt(i);
                if (oc != null)
                {
                    foreach (var mesh in oc.Meshes)
                    foreach (var s in mesh.Sections)
                    foreach (var v in s.Verts) { sea = v.Position.Z; break; }
                    if (!float.IsNaN(sea)) { /* flat plane -- one vertex is enough */ }
                }
            }
        }

        // Determine world size from the tile grid (corner offsets are multiples of 256).
        foreach (int i in tileIdx)
        {
            var sys = model.FetchMeshSystemAt(i);
            if (sys == null) continue;
            var off = sys.WorldOffset;
            maxCorner = Math.Max(maxCorner, Math.Max((int)off.X, (int)off.Y));
        }
        int worldSize = maxCorner + 256;
        if (worldSize <= 256) worldSize = 8192; // degenerate guard
        if (float.IsNaN(sea)) sea = 0f;         // no ocean plane -> nothing is below sea

        float cell = (float)worldSize / n;
        var maxZ = new float[n * n];
        var covered = new bool[n * n];
        Array.Fill(maxZ, float.NegativeInfinity);

        int tiles = 0;
        foreach (int i in tileIdx)
        {
            var sys = model.FetchMeshSystemAt(i);
            if (sys == null) continue;
            tiles++;
            Vector3 off = sys.WorldOffset;
            foreach (var mesh in sys.Meshes)
            foreach (var s in mesh.Sections)
            {
                RasterizeSection(s, off, n, cell, maxZ, covered);
            }
        }

        // The terrain tiles are irregular TINs, so a cell centre can fall in a gap between triangles
        // even on solid high ground. Defaulting those uncovered cells to water would carve spurious
        // lakes/inlets right where facilities sit. Instead, flood every uncovered cell with the height
        // of its nearest COVERED cell (multi-source BFS) before thresholding: interior gaps inherit the
        // surrounding highland (-> land), while genuine open ocean inherits the low ocean-floor tiles
        // that do rasterize (-> water).
        FillUncovered(maxZ, covered, n);

        // Water = terrain (after gap-fill) below sea level.
        var packed = new byte[(n * n + 7) / 8];
        int waterCells = 0;
        for (int idx = 0; idx < n * n; idx++)
        {
            if (maxZ[idx] < sea)
            {
                packed[idx >> 3] |= (byte)(1 << (idx & 7));
                waterCells++;
            }
        }

        return new ContinentTerrain
        {
            N = n,
            WorldSize = worldSize,
            SeaLevel = sea,
            TileCount = tiles,
            WaterCells = waterCells,
            PackedMask = packed
        };
    }

    // Multi-source BFS: propagate each covered cell's height outward so every uncovered cell takes the
    // height of the nearest covered cell (4-connected). Fully fills the grid in one sweep.
    private static void FillUncovered(float[] maxZ, bool[] covered, int n)
    {
        var q = new Queue<int>();
        for (int idx = 0; idx < n * n; idx++)
        {
            if (covered[idx]) q.Enqueue(idx);
        }
        if (q.Count == 0 || q.Count == n * n) return;

        while (q.Count > 0)
        {
            int idx = q.Dequeue();
            int i = idx % n, j = idx / n;
            float z = maxZ[idx];
            void Visit(int ni, int nj)
            {
                if (ni < 0 || nj < 0 || ni >= n || nj >= n) return;
                int m = nj * n + ni;
                if (covered[m]) return;
                covered[m] = true;   // mark as filled so it's enqueued once
                maxZ[m] = z;
                q.Enqueue(m);
            }
            Visit(i - 1, j);
            Visit(i + 1, j);
            Visit(i, j - 1);
            Visit(i, j + 1);
        }
    }

    private static void RasterizeSection(UberModel.MeshSection s, Vector3 off, int n, float cell,
        float[] maxZ, bool[] covered)
    {
        UberModel.UberVert[] v = s.Verts;
        ushort[] idx = s.Indices;
        if (v.Length == 0 || idx.Length < 3) return;

        if (s.IsTriStrip)
        {
            for (int k = 0; k + 2 < idx.Length; k++)
            {
                int a = idx[k], b = idx[k + 1], c = idx[k + 2];
                if (a == b || b == c || a == c) continue;          // degenerate strip joint
                // Preserve consistent winding across the strip (irrelevant to rasterization, kept for clarity).
                if ((k & 1) == 0) Tri(v[a], v[b], v[c], off, n, cell, maxZ, covered);
                else Tri(v[b], v[a], v[c], off, n, cell, maxZ, covered);
            }
        }
        else
        {
            for (int k = 0; k + 2 < idx.Length; k += 3)
            {
                Tri(v[idx[k]], v[idx[k + 1]], v[idx[k + 2]], off, n, cell, maxZ, covered);
            }
        }
    }

    // Scan-convert one triangle into the grid, writing the barycentric-interpolated Z (keeping the
    // per-cell max) for every cell whose centre falls inside it.
    private static void Tri(UberModel.UberVert va, UberModel.UberVert vb, UberModel.UberVert vc,
        Vector3 off, int n, float cell, float[] maxZ, bool[] covered)
    {
        float ax = va.Position.X + off.X, ay = va.Position.Y + off.Y, az = va.Position.Z;
        float bx = vb.Position.X + off.X, by = vb.Position.Y + off.Y, bz = vb.Position.Z;
        float cx = vc.Position.X + off.X, cy = vc.Position.Y + off.Y, cz = vc.Position.Z;

        float minX = MathF.Min(ax, MathF.Min(bx, cx));
        float maxX = MathF.Max(ax, MathF.Max(bx, cx));
        float minY = MathF.Min(ay, MathF.Min(by, cy));
        float maxY = MathF.Max(ay, MathF.Max(by, cy));

        int i0 = Math.Max(0, (int)MathF.Floor(minX / cell));
        int i1 = Math.Min(n - 1, (int)MathF.Floor(maxX / cell));
        int j0 = Math.Max(0, (int)MathF.Floor(minY / cell));
        int j1 = Math.Min(n - 1, (int)MathF.Floor(maxY / cell));
        if (i0 > i1 || j0 > j1) return;

        float d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
        if (MathF.Abs(d) < 1e-9f) return; // degenerate

        for (int j = j0; j <= j1; j++)
        {
            float py = (j + 0.5f) * cell;
            for (int i = i0; i <= i1; i++)
            {
                float px = (i + 0.5f) * cell;
                float w0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / d;
                float w1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / d;
                float w2 = 1f - w0 - w1;
                if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                float z = w0 * az + w1 * bz + w2 * cz;
                int m = j * n + i;
                covered[m] = true;
                if (z > maxZ[m]) maxZ[m] = z;
            }
        }
    }
}
