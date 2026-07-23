using System.Text;
using RaxicoreEditor.EngineAssets.Archives;
using RaxicoreEditor.EngineAssets.Surfaces;

/// <summary>
/// Reads a continent's per-tile surface grids (<c>mapNN_srf.pak</c>) and rasterizes the lava surface
/// types into an <c>N x N</c> mask.
///
/// The pak's first entry (<c>mapNN.srf</c>) is a small chunky AsciiDatabase carrying the ordered
/// surface-type NAME list (a per-continent <c>groundcover</c> resource); every other entry is a
/// <c>mapNN&lt;CC&gt;&lt;RR&gt;.srf</c> tile of 128x128 cells whose <c>type</c> byte indexes that
/// list. We name-match the volcanic lava types (<c>lava</c> / <c>volcanic_scorched</c> /
/// <c>SCORCHED</c>) at the grid's native 2-world-unit resolution, then downsample to the requested
/// mask (a cell is set if ANY sub-cell is lava).
///
/// The open sea is NOT a surface type here (ocean floor is typed <c>*_shore</c>), so the coastline
/// still comes from the terrain heightfield; this only supplies the on-land lava overlay. (The
/// <c>road</c> surface type is present but its cell coverage is inconsistent/implausible across
/// continents -- e.g. ~38% of arctic Esamir -- so roads are NOT extracted here.)
/// </summary>
internal sealed class ContinentSurface
{
    public int N { get; private init; }
    public int LavaCells { get; private init; }
    public byte[] LavaMask { get; private init; } = Array.Empty<byte>();

    public static ContinentSurface Build(string srfPakPath, string baseName, int n, int worldSize)
    {
        var pak = PakArchive.Load(File.ReadAllBytes(srfPakPath));

        string[] names = ReadTypeNames(pak, baseName);
        var lavaTypes = new HashSet<int>();
        for (int i = 0; i < names.Length; i++)
        {
            string s = names[i];
            if (s == "lava" || s == "volcanic_scorched" || s == "SCORCHED" || s == "molten") lavaTypes.Add(i);
        }

        var lava = new bool[n * n];
        int cell = worldSize / n;

        foreach (var e in pak.Entries)
        {
            if (!e.Name.EndsWith(".srf", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(e.Name, baseName + ".srf", StringComparison.OrdinalIgnoreCase)) continue;

            string stem = e.Name[..^4]; // mapNNCCRR
            if (stem.Length < 9) continue;
            if (!int.TryParse(stem.AsSpan(5, 2), out int col) || !int.TryParse(stem.AsSpan(7, 2), out int row)) continue;

            SurfaceTile tile;
            try { tile = SurfaceTile.Parse(pak.Extract(e.Name)); }
            catch { continue; }
            if (!tile.IsFull) continue;

            for (int r = 0; r < SurfaceTile.GridDim; r++)
            {
                int wy = row * 256 + r * 2;             // grid row -> world +Y (north)
                int gj = Math.Min(n - 1, wy / cell);
                for (int c = 0; c < SurfaceTile.GridDim; c++)
                {
                    if (!lavaTypes.Contains(tile.GetCell(r, c).Type)) continue;
                    int wx = col * 256 + c * 2;          // grid col -> world +X (east)
                    int gi = Math.Min(n - 1, wx / cell);
                    lava[gj * n + gi] = true;
                }
            }
        }

        var lavaPacked = new byte[(n * n + 7) / 8];
        int lc = 0;
        for (int m = 0; m < n * n; m++)
        {
            if (lava[m]) { lavaPacked[m >> 3] |= (byte)(1 << (m & 7)); lc++; }
        }

        return new ContinentSurface { N = n, LavaCells = lc, LavaMask = lavaPacked };
    }

    // The mapNN.srf meta entry: a chunky AsciiDatabase whose single `groundcover` resource is the
    // surface-type name list. It follows the same chunky section grammar as the .mpo: the keyword
    // sits in a fixed 16-byte NUL-padded field, then u16 flag, u32 byteLen, then the payload
    // (u32 count + `count` NUL-terminated names). Parsed deterministically off the keyword field so
    // the indices line up exactly (byte-verified layout, groundcover-surfaces.md §3).
    private const int KeywordField = 16;

    private static string[] ReadTypeNames(PakArchive pak, string baseName)
    {
        int idx = pak.IndexOf(baseName + ".srf");
        if (idx < 0) return Array.Empty<string>();
        byte[] d = pak.Extract(idx);

        ReadOnlySpan<byte> kw = "groundcover"u8;
        int g = -1;
        for (int i = 0; i + kw.Length <= d.Length; i++)
        {
            if (d.AsSpan(i, kw.Length).SequenceEqual(kw)) { g = i; break; }
        }
        if (g < 0 || g + KeywordField + 10 > d.Length) return Array.Empty<string>();

        int p = g + KeywordField; // skip the 16-byte keyword field
        p += 2;                   // u16 flag
        p += 4;                   // u32 byteLen
        uint count = (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
        p += 4;
        if (count is < 1 or > 64) return Array.Empty<string>();

        var list = new List<string>((int)count);
        int q = p;
        for (int k = 0; k < count && q < d.Length; k++)
        {
            int s = q;
            while (q < d.Length && d[q] != 0) q++;
            if (q >= d.Length) break;
            list.Add(Encoding.ASCII.GetString(d, s, q - s));
            q++;
        }
        return list.ToArray();
    }
}
