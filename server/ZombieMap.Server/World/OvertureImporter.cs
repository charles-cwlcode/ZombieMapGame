using System.Diagnostics;
using System.Globalization;
using DuckDB.NET.Data;
using Npgsql;
using NpgsqlTypes;

namespace ZombieMap.Server.World;

/// <summary>
/// Copies one area's buildings and places from Overture Maps (public GeoParquet on S3) into PostGIS.
/// DuckDB reads only the parquet row groups whose bounding boxes overlap the area, so a town takes
/// about a minute. Buildings are upserted by GERS ID, so re-importing is safe.
/// </summary>
public sealed class OvertureImporter(NpgsqlDataSource db, ILogger<OvertureImporter> log)
{
    private const string Bucket = "s3://overturemaps-us-west-2/release";

    public async Task<string> ResolveReleaseAsync(string configured, CancellationToken ct)
    {
        if (!string.Equals(configured, "latest", StringComparison.OrdinalIgnoreCase)) return configured;

        using var duck = OpenDuck();
        using var cmd = duck.CreateCommand();
        // Overture keeps the last few releases. Listing one file per release is enough to find them.
        cmd.CommandText = $"""
            SELECT max(regexp_extract(file, 'release/([^/]+)/', 1))
            FROM glob('{Bucket}/*/theme=buildings/type=building/part-00000*')
            """;
        var latest = (string?)await Task.Run(() => cmd.ExecuteScalar(), ct);
        if (string.IsNullOrEmpty(latest)) throw new InvalidOperationException("Couldn't find an Overture release on S3.");
        return latest;
    }

    public async Task ImportAsync(MapArea area, string release, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        log.LogInformation("Importing Overture {Release} for {Area}. This takes about a minute…", release, area);

        string bbox = string.Format(CultureInfo.InvariantCulture,
            "bbox.xmin <= {2} AND bbox.xmax >= {0} AND bbox.ymin <= {3} AND bbox.ymax >= {1}",
            area.West, area.South, area.East, area.North);

        await using var conn = await db.OpenConnectionAsync(ct);
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await Exec(conn, """
                CREATE TEMP TABLE stage_buildings (id text, name text, class text, subtype text, height real,
                    num_floors int, wkb bytea, source text, license text) ON COMMIT DROP;
                CREATE TEMP TABLE stage_places (id text, name text, category text, hierarchy text,
                    confidence real, wkb bytea) ON COMMIT DROP;
                """, ct);

            int buildings = await CopyAsync(conn, release, "buildings/type=building", $"""
                SELECT id, names."primary", class, subtype, CAST(height AS REAL), num_floors,
                       ST_AsWKB(geometry), sources[1].dataset, sources[1].license
                FROM __SOURCE__ WHERE {bbox} AND coalesce(is_underground, false) = false
                """,
                "COPY stage_buildings (id, name, class, subtype, height, num_floors, wkb, source, license) FROM STDIN (FORMAT BINARY)",
                [NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Real,
                 NpgsqlDbType.Integer, NpgsqlDbType.Bytea, NpgsqlDbType.Text, NpgsqlDbType.Text], ct);
            log.LogInformation("  {Count} buildings read ({Seconds:0}s)", buildings, sw.Elapsed.TotalSeconds);

            int places = await CopyAsync(conn, release, "places/type=place", $"""
                SELECT id, names."primary", basic_category, array_to_string(taxonomy.hierarchy, '/'),
                       CAST(confidence AS REAL), ST_AsWKB(geometry)
                FROM __SOURCE__ WHERE {bbox}
                """,
                "COPY stage_places (id, name, category, hierarchy, confidence, wkb) FROM STDIN (FORMAT BINARY)",
                [NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Text, NpgsqlDbType.Real,
                 NpgsqlDbType.Bytea], ct);
            log.LogInformation("  {Count} places read ({Seconds:0}s)", places, sw.Elapsed.TotalSeconds);

            await Exec(conn, """
                INSERT INTO buildings AS b (id, name, class, subtype, height, num_floors, geom, area_m2, source, license, release)
                SELECT id, name, class, subtype, height, num_floors, g, ST_Area(g::geography), source, license, @release
                FROM (SELECT *, ST_SetSRID(ST_GeomFromWKB(wkb), 4326) AS g FROM stage_buildings) s
                WHERE GeometryType(g) IN ('POLYGON', 'MULTIPOLYGON')
                ON CONFLICT (id) DO UPDATE SET
                    name = EXCLUDED.name, class = EXCLUDED.class, subtype = EXCLUDED.subtype,
                    height = EXCLUDED.height, num_floors = EXCLUDED.num_floors, geom = EXCLUDED.geom,
                    area_m2 = EXCLUDED.area_m2, source = EXCLUDED.source, license = EXCLUDED.license,
                    release = EXCLUDED.release;

                INSERT INTO places (id, name, category, hierarchy, confidence, geom, release)
                SELECT id, name, category, hierarchy, confidence, ST_SetSRID(ST_GeomFromWKB(wkb), 4326), @release
                FROM stage_places
                ON CONFLICT (id) DO UPDATE SET
                    name = EXCLUDED.name, category = EXCLUDED.category, hierarchy = EXCLUDED.hierarchy,
                    confidence = EXCLUDED.confidence, geom = EXCLUDED.geom, release = EXCLUDED.release;
                """, ct, ("release", release));

            var areaParams = AreaParams(area);
            await Classify(conn, areaParams, ct);

            await Exec(conn, """
                INSERT INTO world_areas (name, west, south, east, north, release, building_count, place_count)
                VALUES (@name, @west, @south, @east, @north, @release, @buildings, @places)
                ON CONFLICT (name) DO UPDATE SET west = EXCLUDED.west, south = EXCLUDED.south,
                    east = EXCLUDED.east, north = EXCLUDED.north, release = EXCLUDED.release,
                    building_count = EXCLUDED.building_count, place_count = EXCLUDED.place_count, imported_at = now();
                """, ct, [.. areaParams, ("name", area.Name), ("release", release), ("buildings", buildings), ("places", places)]);

            await tx.CommitAsync(ct);
        }

        await using (var cmd = new NpgsqlCommand("ANALYZE buildings; ANALYZE places;", conn))
            await cmd.ExecuteNonQueryAsync(ct);

        log.LogInformation("Imported {Area} in {Seconds:0}s", area.Name, sw.Elapsed.TotalSeconds);
    }

    /// <summary>Re-applies the building-type rules (WorldSchema) to one already-imported area.</summary>
    public async Task ClassifyAsync(MapArea area, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Classify(conn, AreaParams(area), ct);
        await tx.CommitAsync(ct);
    }

    private static async Task Classify(NpgsqlConnection conn, (string, object)[] areaParams, CancellationToken ct)
    {
        await Exec(conn, WorldSchema.ClassifyFromClassSql, ct, areaParams);
        await Exec(conn, WorldSchema.ClassifyFromPlacesSql, ct, areaParams);
    }

    private static (string, object)[] AreaParams(MapArea area) =>
    [
        ("west", area.West), ("south", area.South), ("east", area.East), ("north", area.North),
    ];

    /// <summary>Runs a DuckDB query over one Overture theme and streams the rows into a Postgres COPY.</summary>
    private static async Task<int> CopyAsync(NpgsqlConnection conn, string release, string theme, string selectTemplate,
        string copySql, NpgsqlDbType[] types, CancellationToken ct)
    {
        string source = $"read_parquet('{Bucket}/{release}/theme={theme}/*', hive_partitioning = 1)";
        using var duck = OpenDuck();
        using var cmd = duck.CreateCommand();
        cmd.CommandText = selectTemplate.Replace("__SOURCE__", source);

        // DuckDB's reader is synchronous; run it off the request threads.
        return await Task.Run(async () =>
        {
            using var reader = cmd.ExecuteReader();
            await using var writer = await conn.BeginBinaryImportAsync(copySql, ct);
            int rows = 0;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                await writer.StartRowAsync(ct);
                for (int i = 0; i < types.Length; i++)
                {
                    if (reader.IsDBNull(i)) await writer.WriteNullAsync(ct);
                    else if (types[i] == NpgsqlDbType.Bytea) await writer.WriteAsync(ReadBytes(reader, i), types[i], ct);
                    else await writer.WriteAsync(reader.GetValue(i), types[i], ct);
                }
                rows++;
            }
            await writer.CompleteAsync(ct);
            return rows;
        }, ct);
    }

    private static byte[] ReadBytes(System.Data.Common.DbDataReader reader, int i)
    {
        var value = reader.GetValue(i);
        if (value is byte[] bytes) return bytes;
        using var stream = value as Stream ?? reader.GetStream(i);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static DuckDBConnection OpenDuck()
    {
        var duck = new DuckDBConnection("DataSource=:memory:");
        duck.Open();
        using var cmd = duck.CreateCommand();
        // httpfs reads S3; spatial gives ST_AsWKB. Both are fetched from DuckDB's extension site once, then cached.
        cmd.CommandText = "INSTALL httpfs; LOAD httpfs; INSTALL spatial; LOAD spatial; SET s3_region = 'us-west-2';";
        cmd.ExecuteNonQuery();
        return duck;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, CancellationToken ct, params (string name, object value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 600 };
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
