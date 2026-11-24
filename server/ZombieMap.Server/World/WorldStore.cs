using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Npgsql;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Protocol;
using ZombieMap.Server.Grains;

namespace ZombieMap.Server.World;

/// <summary>
/// Read side of the real-world map data in PostGIS. Calls wait (briefly) for startup to finish;
/// if the database never came up, they fail with a "world_offline" game error and the rest of the
/// server (encounters, dev floor plans) keeps working.
/// </summary>
public sealed class WorldStore(NpgsqlDataSource db, IOptions<WorldOptions> options)
{
    private const int MaxOutlinePoints = 200;
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(5);

    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WorldOptions _options = options.Value;

    public string Status { get; private set; } = "starting";

    public void MarkReady() { Status = "ready"; _ready.TrySetResult(true); }
    public void MarkLoading(string status) => Status = status;
    public void MarkOffline(string reason) { Status = "offline: " + reason; _ready.TrySetResult(false); }

    /// <summary>The building at (or within a few metres of) a point, plus whether the player can loot it.</summary>
    public async Task<BuildingInfo?> FindBuildingAtAsync(LatLng point, LatLng player, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await using var cmd = db.CreateCommand("""
            WITH tap AS (SELECT ST_SetSRID(ST_MakePoint(@lng, @lat), 4326) AS g),
                 me  AS (SELECT ST_SetSRID(ST_MakePoint(@plng, @plat), 4326) AS g)
            SELECT b.id, b.kind, b.name,
                   ST_AsGeoJSON(ST_SimplifyPreserveTopology(zm_outer_ring(b.geom), 0.000003)),
                   ST_Distance(b.geom::geography, me.g::geography)
            FROM buildings b, tap, me
            WHERE b.geom && ST_Expand(tap.g, 0.0002)
              AND ST_DWithin(b.geom::geography, tap.g::geography, @tolerance)
            ORDER BY ST_Distance(b.geom::geography, tap.g::geography), b.area_m2
            LIMIT 1
            """);
        cmd.Parameters.AddWithValue("lat", point.Lat);
        cmd.Parameters.AddWithValue("lng", point.Lng);
        cmd.Parameters.AddWithValue("plat", player.Lat);
        cmd.Parameters.AddWithValue("plng", player.Lng);
        cmd.Parameters.AddWithValue("tolerance", _options.TapToleranceMetres);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        double distance = reader.GetDouble(4);
        return new BuildingInfo
        {
            BuildingId = reader.GetString(0),
            Kind = ParseKind(reader.GetString(1)),
            Name = reader.IsDBNull(2) ? null : reader.GetString(2),
            Outline = ParseRing(reader.GetString(3)),
            DistanceMetres = Math.Round(distance, 1),
            LootRangeMetres = _options.LootRangeMetres,
            InRange = distance <= _options.LootRangeMetres,
        };
    }

    /// <summary>A building's footprint in local metres, ready for the floor-plan generator. Null if unknown.</summary>
    public async Task<BuildingFootprint?> GetFootprintAsync(string buildingId, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await using var cmd = db.CreateCommand("""
            SELECT kind, num_floors, ST_AsGeoJSON(ST_SimplifyPreserveTopology(zm_outer_ring(geom), 0.000003))
            FROM buildings WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("id", buildingId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var ring = ParseRing(reader.GetString(2));
        var origin = GeoMath.Centre(ring);
        return new BuildingFootprint
        {
            BuildingId = buildingId,
            Kind = ParseKind(reader.GetString(0)),
            Levels = reader.IsDBNull(1) ? 1 : Math.Clamp(reader.GetInt32(1), 1, 50),
            Points = ring.Select(p => GeoMath.ToLocal(origin, p)).ToList(),
        };
    }

    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        bool ok;
        try { ok = await _ready.Task.WaitAsync(ReadyWait, ct); }
        catch (TimeoutException) { throw new GameRuleException("world_loading", $"Map data is still loading ({Status}). Try again in a moment."); }
        if (!ok) throw new GameRuleException("world_offline", "Map data isn't available on this server.");
    }

    private static BuildingKind ParseKind(string kind) =>
        Enum.TryParse<BuildingKind>(kind, out var k) ? k : BuildingKind.Unknown;

    /// <summary>GeoJSON LineString → points, without the repeated closing point.</summary>
    private static List<LatLng> ParseRing(string geoJson)
    {
        var coords = (JArray)JObject.Parse(geoJson)["coordinates"]!;
        var points = coords.Select(c => new LatLng((double)c[1]!, (double)c[0]!)).ToList();
        if (points.Count > 1 && points[0].Lat == points[^1].Lat && points[0].Lng == points[^1].Lng)
            points.RemoveAt(points.Count - 1);
        if (points.Count > MaxOutlinePoints)
        {
            int step = (int)Math.Ceiling(points.Count / (double)MaxOutlinePoints);
            points = points.Where((_, i) => i % step == 0).ToList();
        }
        return points;
    }
}
