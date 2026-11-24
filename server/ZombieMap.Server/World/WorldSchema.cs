namespace ZombieMap.Server.World;

/// <summary>PostGIS tables for real-world map data. Idempotent: safe to run on every start.</summary>
public static class WorldSchema
{
    public const string Sql = """
        CREATE EXTENSION IF NOT EXISTS postgis;

        -- Buildings from Overture Maps. id is the Overture GERS ID, which stays stable across releases.
        CREATE TABLE IF NOT EXISTS buildings (
            id          text PRIMARY KEY,
            name        text,
            class       text,
            subtype     text,
            height      real,
            num_floors  int,
            geom        geometry(Geometry, 4326) NOT NULL,
            area_m2     real,
            kind        text NOT NULL DEFAULT 'Unknown',  -- ZombieMap.Core.Buildings.BuildingKind
            kind_source text,                            -- 'class', 'place' or 'size'
            source      text,                            -- e.g. OpenStreetMap, Microsoft ML Buildings
            license     text,
            release     text
        );
        CREATE INDEX IF NOT EXISTS buildings_geom_idx ON buildings USING gist (geom);

        -- Places (shops, stations, …) are only used to decide what kind of building something is.
        CREATE TABLE IF NOT EXISTS places (
            id         text PRIMARY KEY,
            name       text,
            category   text,
            hierarchy  text,   -- Overture taxonomy, '/'-joined, e.g. shopping/specialty_store/pharmacy_and_drug_store/pharmacy
            confidence real,
            geom       geometry(Point, 4326) NOT NULL,
            release    text
        );
        CREATE INDEX IF NOT EXISTS places_geom_idx ON places USING gist (geom);

        CREATE TABLE IF NOT EXISTS world_areas (
            name           text PRIMARY KEY,
            west           double precision NOT NULL,
            south          double precision NOT NULL,
            east           double precision NOT NULL,
            north          double precision NOT NULL,
            release        text NOT NULL,
            building_count int NOT NULL,
            place_count    int NOT NULL,
            imported_at    timestamptz NOT NULL DEFAULT now()
        );

        -- Largest polygon's outer wall (buildings can be multipolygons).
        CREATE OR REPLACE FUNCTION zm_outer_ring(g geometry) RETURNS geometry
        LANGUAGE sql IMMUTABLE AS $$
            SELECT ST_ExteriorRing(d.geom) FROM ST_Dump(g) d ORDER BY ST_Area(d.geom) DESC LIMIT 1
        $$;
        """;

    /// <summary>
    /// Sets buildings.kind for one area. Order matters: building class first, then size, then
    /// places inside the footprint override (a pharmacy inside a "house" is a pharmacy).
    /// </summary>
    public const string ClassifyFromClassSql = """
        WITH area AS (SELECT ST_MakeEnvelope(@west, @south, @east, @north, 4326) AS g)
        UPDATE buildings b SET
            kind = CASE
                WHEN b.class = 'train_station' THEN 'Station'
                WHEN b.class IN ('house','detached','semidetached_house','terrace','bungalow','residential',
                                 'apartments','dormitory','farm','cabin','static_caravan','houseboat') THEN 'House'
                WHEN b.class IN ('retail','supermarket','kiosk') THEN 'Shop'
                WHEN b.class IN ('office','commercial') THEN 'Office'
                WHEN b.class IS NOT NULL THEN 'Unknown'          -- garages, sheds, churches, schools, …
                WHEN b.subtype = 'residential' THEN 'House'
                WHEN b.subtype = 'commercial' THEN 'Office'
                WHEN b.subtype IS NULL AND b.area_m2 BETWEEN 35 AND 300 THEN 'House'  -- most unlabelled UK buildings are homes
                ELSE 'Unknown'
            END,
            kind_source = CASE WHEN b.class IS NOT NULL OR b.subtype IS NOT NULL THEN 'class' ELSE 'size' END
        FROM area WHERE b.geom && area.g;
        """;

    /// <summary>Second pass: places inside (or, for stations, next to) a building set its kind.</summary>
    public const string ClassifyFromPlacesSql = """
        WITH area AS (SELECT ST_MakeEnvelope(@west, @south, @east, @north, 4326) AS g),
        tagged AS (
            SELECT p.id, p.name, p.geom,
                   CASE
                       -- Stations unlock train travel, so only trust confident ones
                       -- (the first test area had a mis-placed station point at 0.71).
                       WHEN p.hierarchy LIKE '%train_station%' AND p.confidence >= 0.8 THEN 'Station'
                       WHEN p.hierarchy LIKE '%train_station%'           THEN NULL
                       WHEN p.hierarchy LIKE '%pharmacy_and_drug_store%' THEN 'Pharmacy'
                       WHEN p.hierarchy LIKE 'shopping/%' OR p.hierarchy LIKE 'food_and_drink/%' THEN 'Shop'
                       WHEN p.hierarchy LIKE 'services_and_business/%'   THEN 'Office'
                   END AS kind,
                   CASE
                       WHEN p.hierarchy LIKE '%train_station%'           THEN 1
                       WHEN p.hierarchy LIKE '%pharmacy_and_drug_store%' THEN 2
                       WHEN p.hierarchy LIKE 'shopping/%' OR p.hierarchy LIKE 'food_and_drink/%' THEN 3
                       ELSE 4
                   END AS priority
            FROM places p, area
            WHERE p.geom && area.g AND p.confidence >= 0.4
        ),
        hits_raw AS (
            SELECT b.id, t.kind, t.name, t.priority, 0.0 AS dist
            FROM tagged t JOIN buildings b ON ST_Contains(b.geom, t.geom)
            WHERE t.kind IS NOT NULL
            UNION ALL
            -- Stations are often mapped as a point on the platform: give them the nearest
            -- station-sized building within 40 m (not a warehouse next to the tracks).
            SELECT n.id, t.kind, t.name, t.priority, n.dist
            FROM tagged t
            CROSS JOIN LATERAL (
                SELECT b.id, ST_Distance(b.geom::geography, t.geom::geography) AS dist
                FROM buildings b
                WHERE b.geom && ST_Expand(t.geom, 0.001)
                  AND ST_DWithin(b.geom::geography, t.geom::geography, 40)
                  AND b.area_m2 < 2000
                  AND coalesce(b.class, '') NOT IN ('industrial', 'warehouse', 'house', 'detached', 'semidetached_house', 'terrace')
                ORDER BY b.geom <-> t.geom LIMIT 1
            ) n
            WHERE t.kind = 'Station'
        ),
        hits AS (
            SELECT DISTINCT ON (id) id, kind, name FROM hits_raw ORDER BY id, priority, dist
        )
        UPDATE buildings b SET kind = hits.kind, kind_source = 'place', name = COALESCE(b.name, hits.name)
        FROM hits WHERE b.id = hits.id;
        """;
}
