// Continent map-grid exporter.
//
// Reads each continent's `contents_mapNN.mpo` (via the reference client's map-resources PAK
// archives) and emits one JSON file per continent describing the coarse 32-wide cell grid:
// which cells are water, which are lakes/lava, and which are terrain sections. This is the
// data the PSFPortal continent modal renders as an SVG.
//
// Usage:
//   dotnet run --project tools/ContinentExport -- --planetside <PlanetSideDir> --out <OutDir>
//
// PlanetSideDir is the read-only reference client folder. Nothing under it is written.

using System.Text.Json;
using System.Text.RegularExpressions;
using RaxicoreEditor.EngineAssets.Archives;
using RaxicoreEditor.EngineAssets.Maps;

static string? Arg(string[] a, string name)
{
    int i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

string planetside = Arg(args, "--planetside") ?? "";
string outDir = Arg(args, "--out") ?? "";
if (planetside.Length == 0 || outDir.Length == 0)
{
    Console.Error.WriteLine("usage: --planetside <dir> --out <dir>");
    return 1;
}

Directory.CreateDirectory(outDir);

// The overworld continents (map01..map15) share one archive; each battle island ships its own.
var paks = new List<string>();
string overworld = Path.Combine(planetside, "maps", "map_resources.pak");
if (File.Exists(overworld)) paks.Add(overworld);
foreach (var d in Directory.EnumerateDirectories(Path.Combine(planetside, "patchmap")).OrderBy(x => x))
{
    foreach (var p in Directory.EnumerateFiles(d, "*_resources.pak")) paks.Add(p);
}

var mpoName = new Regex(@"^contents_(map\d+)\.mpo$", RegexOptions.IgnoreCase);
int exported = 0;

Console.WriteLine($"{"continent",-10} {"water",7} {"lakes",7} {"sections",9} {"objects",8}  grid");
Console.WriteLine(new string('-', 60));

foreach (var pakPath in paks)
{
    PakArchive pak;
    try { pak = PakArchive.Load(File.ReadAllBytes(pakPath)); }
    catch (Exception e) { Console.Error.WriteLine($"skip {pakPath}: {e.Message}"); continue; }

    foreach (var entry in pak.Entries)
    {
        var m = mpoName.Match(entry.Name);
        if (!m.Success) continue;
        string baseName = m.Groups[1].Value.ToLowerInvariant();

        MpoFile mpo;
        try { mpo = MpoFile.Parse(pak.Extract(entry.Name)); }
        catch (Exception e) { Console.Error.WriteLine($"skip {entry.Name}: {e.Message}"); continue; }

        // Grid dimensions come from map_sections + map_water, the two layers confirmed to pack as
        // (col = id & 0x1F, row = id >> 5). map_lakes does NOT use that packing -- its payload
        // unpacks to nonsense rows (~25M), so it is per-lake geometry, not a cell grid, and is not
        // lava. Lava lives in the per-tile surface (.srf) data, not the MPO, so it is not exported
        // here.
        int maxCol = 0, maxRow = 0;
        void Bounds(IReadOnlyList<uint> ids)
        {
            foreach (var id in ids)
            {
                var (c, r) = MpoFile.UnpackCell(id);
                if (c > maxCol) maxCol = c;
                if (r > maxRow) maxRow = r;
            }
        }
        Bounds(mpo.TerrainTileIds);
        Bounds(mpo.WaterCellIds);

        int cols = maxCol + 1;
        int rows = maxRow + 1;

        static int[][] Cells(IReadOnlyList<uint> ids) =>
            ids.Select(id => { var (c, r) = MpoFile.UnpackCell(id); return new[] { c, r }; }).ToArray();

        var doc = new
        {
            @base = baseName,
            cols,
            rows,
            worldSize = 8192,
            cell = 8192 / Math.Max(cols, rows),
            water = Cells(mpo.WaterCellIds),
            sections = Cells(mpo.TerrainTileIds)
        };

        string outPath = Path.Combine(outDir, baseName + ".json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false }));
        exported++;

        Console.WriteLine($"{baseName,-10} {mpo.WaterCellIds.Count,7} {mpo.LakeCellIds.Count,7} " +
                          $"{mpo.TerrainTileIds.Count,9} {mpo.Objects.Count,8}  {cols}x{rows}");
    }
}

Console.WriteLine(new string('-', 60));
Console.WriteLine($"exported {exported} continents to {outDir}");
return 0;
