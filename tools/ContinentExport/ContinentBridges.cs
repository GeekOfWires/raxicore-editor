using RaxicoreEditor.EngineAssets.Archives;
using RaxicoreEditor.EngineAssets.Maps;

/// <summary>
/// Extracts bridge deck runs from a continent's <c>contents_mapNN.mpo</c> object list.
///
/// Bridges ship as long ordered chains of small placed pieces (<c>bridge_start</c> / <c>bridge_mid</c>
/// / <c>bridge_end</c> / <c>bridge_extend</c>, ~16 world units apart; <c>bridge_support_foot</c> pillars
/// are ignored). The pieces are contiguous and in-order within the object list, so we walk the deck
/// pieces and split into a new polyline whenever the gap to the previous piece exceeds a threshold --
/// yielding one clean polyline per bridge for a map overlay.
/// </summary>
internal static class ContinentBridges
{
    private static readonly HashSet<string> Deck = new(StringComparer.OrdinalIgnoreCase)
    {
        "bridge_start", "bridge_mid", "bridge_end", "bridge_extend"
    };

    // Deck pieces sit ~16u apart; a jump much larger than that means a different bridge.
    private const float GapSq = 64f * 64f;

    /// <summary>Returns bridge polylines as arrays of [x, y] world points (empty if none / no MPO).</summary>
    public static List<List<float[]>> Build(string mapResourcesPak, string baseName)
    {
        var bridges = new List<List<float[]>>();
        MpoFile mpo;
        try
        {
            var pak = PakArchive.Load(File.ReadAllBytes(mapResourcesPak));
            int idx = pak.IndexOf("contents_" + baseName + ".mpo");
            if (idx < 0) return bridges;
            mpo = MpoFile.Parse(pak.Extract(idx));
        }
        catch { return bridges; }

        List<float[]>? cur = null;
        float px = 0, py = 0;
        foreach (var o in mpo.Objects)
        {
            if (!Deck.Contains(o.Name)) continue;
            float x = o.Position.X, y = o.Position.Y;
            if (cur == null || (x - px) * (x - px) + (y - py) * (y - py) > GapSq)
            {
                cur = new List<float[]>();
                bridges.Add(cur);
            }
            cur.Add(new[] { x, y });
            px = x;
            py = y;
        }

        // Drop stray single-piece runs (noise); a real bridge has at least a few pieces.
        bridges.RemoveAll(b => b.Count < 3);
        return bridges;
    }
}
