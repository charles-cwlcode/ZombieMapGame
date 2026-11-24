using H3;
using H3.Algorithms;
using H3.Extensions;
using NetTopologySuite.Geometries;
using ZombieMap.Core.Geo;

namespace ZombieMap.Server.World;

/// <summary>
/// The H3 hexagon grid zombies live on. Resolution 9: hexagons ~350 m across with ~200 m edges, the
/// same size anywhere on Earth, six neighbours each at the same distance. Cells are named by their
/// H3 index as a hex string (e.g. "89195da49b7ffff"), which is also the zombie cell grain's key.
/// Only the server uses H3; the client gets outlines.
/// </summary>
public static class ZombieGrid
{
    public const int Resolution = 9;

    public static string CellAt(LatLng p) =>
        H3Index.FromLatLng(H3.Model.LatLng.FromCoordinate(new Coordinate(p.Lng, p.Lat)), Resolution).ToString();

    public static bool IsCell(string? cell)
    {
        if (string.IsNullOrEmpty(cell) || cell.Length > 16) return false;
        try
        {
            var index = new H3Index(cell);
            return index.IsValidCell && index.Resolution == Resolution;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The hexagon's corners (5 for the rare pentagon), not closed.</summary>
    public static List<LatLng> Outline(string cell) =>
        new H3Index(cell).GetCellBoundaryVertices().Select(v => new LatLng(v.LatitudeDegrees, v.LongitudeDegrees)).ToList();

    public static LatLng Centre(string cell)
    {
        var c = new H3Index(cell).ToLatLng();
        return new LatLng(c.LatitudeDegrees, c.LongitudeDegrees);
    }

    /// <summary>The cell and every cell within <paramref name="rings"/> steps, with their distance in steps.</summary>
    public static List<(string cell, int distance)> Disk(string cell, int rings) =>
        Rings.GetKRing(new H3Index(cell), rings).Select(r => (r.Index.ToString(), r.Distance)).ToList();

    public static List<string> Neighbours(string cell) =>
        Disk(cell, 1).Where(d => d.distance == 1).Select(d => d.cell).ToList();

    /// <summary>Hordes roam regions: H3 resolution 7 (~1.2 km across, 49 zombie cells).</summary>
    public const int RegionResolution = 7;

    public static string RegionOf(string cell) => new H3Index(cell).GetParentForResolution(RegionResolution).ToString();

    /// <summary>The region and the six around it.</summary>
    public static List<string> RegionsAround(string cell) =>
        Rings.GetKRing(new H3Index(cell).GetParentForResolution(RegionResolution), 1).Select(r => r.Index.ToString()).ToList();

    public static List<string> CellsIn(string region) =>
        new H3Index(region).GetChildrenForResolution(Resolution).Select(c => c.ToString()).ToList();

    /// <summary>Half the width of the square around a cell that holds the cell and the given margin.</summary>
    public static double HalfSpanMetres(string cell, double margin)
    {
        var centre = Centre(cell);
        return Outline(cell).Max(p => GeoMath.Distance(centre, p)) + margin;
    }
}
