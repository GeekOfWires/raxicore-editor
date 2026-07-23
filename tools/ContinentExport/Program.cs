// Continent terrain exporter.
//
// Reads each continent's `mapNN.ubr` (the reference client's per-tile terrain mesh) via the
// faithful UberModel decoder and emits one JSON file per continent describing a coarse water
// mask: for each cell of an `N x N` grid over the continent, whether the terrain there sits
// below sea level (the flat `_oc` ocean-plane height baked into the same `.ubr`).
//
// This is the data the PSFPortal continent modal turns into a coastline (ocean = transparent,
// inland water = blue) behind the facility/SOI overlay. Ocean-vs-lake classification and the
// marching-squares contouring happen downstream in scripts/build-continents.mjs.
//
// Why the mesh and not `contents_mapNN.mpo`: the MPO's `map_water` layer only flags tiles that
// carry an `_oc` water-plane record (coast, rivers and lakes alike -- ~74% of tiles), so it does
// NOT separate ocean from land. The real silhouette is in the per-tile terrain heights, thresholded
// at the `_oc` plane -- verified: the plane is a single flat Z per map (map03 = 29.5) and all 1024
// tiles decode.
//
// Usage:
//   dotnet run --project tools/ContinentExport -- --planetside <PlanetSideDir> --out <OutDir>
//
// PlanetSideDir is the read-only reference client folder. Nothing under it is written.

using System.Text.Json;
using System.Text.RegularExpressions;

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

// Resolution of the exported water mask, in cells per axis. 256 -> 32 world units per cell on a
// full 8192 continent: fine enough for a smooth coastline after downstream contouring, small enough
// that the packed bitmask stays ~8 KB per continent.
const int MaskN = 256;

// Overworld continents ship a loose mapNN.ubr. (Battle islands live under patchmap/ and are not
// part of the portal's continent list, so they are skipped when their .ubr is absent.)
var mapUbr = new Regex(@"^map(\d{2})\.ubr$", RegexOptions.IgnoreCase);
int exported = 0;

Console.WriteLine($"{"continent",-10} {"tiles",6} {"sea",7} {"water%",7}  grid");
Console.WriteLine(new string('-', 52));

foreach (var ubrPath in Directory.EnumerateFiles(planetside, "map*.ubr").OrderBy(x => x))
{
    string file = Path.GetFileName(ubrPath);
    var m = mapUbr.Match(file);
    if (!m.Success) continue;
    string baseName = "map" + m.Groups[1].Value;

    ContinentTerrain terrain;
    try { terrain = ContinentTerrain.Build(ubrPath, MaskN); }
    catch (Exception e) { Console.Error.WriteLine($"skip {file}: {e.Message}"); continue; }

    var doc = new
    {
        @base = baseName,
        worldSize = terrain.WorldSize,
        sea = terrain.SeaLevel,
        maskN = terrain.N,
        // Row-major (j*N + i) bit-packed water mask, base64. bit set == below sea level (water).
        // i indexes world +X (east), j indexes world +Y (north).
        mask = Convert.ToBase64String(terrain.PackedMask)
    };

    File.WriteAllText(Path.Combine(outDir, baseName + ".json"),
        JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false }));
    exported++;

    Console.WriteLine($"{baseName,-10} {terrain.TileCount,6} {terrain.SeaLevel,7:F1} " +
                      $"{100.0 * terrain.WaterCells / (terrain.N * terrain.N),6:F0}%  {terrain.N}x{terrain.N}");
}

Console.WriteLine(new string('-', 52));
Console.WriteLine($"exported {exported} continents to {outDir}");
return 0;
