namespace ZombieMap.Server.World;

/// <summary>"World" section of appsettings. Map areas live in appsettings.Local.json (git-ignored).</summary>
public sealed class WorldOptions
{
    public const string Section = "World";

    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5433;Username=zombiemap;Password=zombiemap;Database=zombiemap";

    /// <summary>Development only: start Docker Desktop and the PostGIS container when the server starts.</summary>
    public bool AutoStartDatabase { get; set; } = true;

    /// <summary>Overture release to import, e.g. "2026-11-18.0", or "latest".</summary>
    public string OvertureRelease { get; set; } = "latest";

    /// <summary>How close the player must be to a building's wall to loot it.</summary>
    public double LootRangeMetres { get; set; } = 80;

    /// <summary>How far from a tap we still count a building as "tapped" (fingers are fat).</summary>
    public double TapToleranceMetres { get; set; } = 6;

    /// <summary>Areas whose buildings and places are imported. Each is imported once, then kept.</summary>
    public List<MapArea> Areas { get; set; } = new();
}

public sealed class MapArea
{
    public string Name { get; set; } = "";
    public double West { get; set; }
    public double South { get; set; }
    public double East { get; set; }
    public double North { get; set; }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name) && West < East && South < North &&
        East - West <= 0.5 && North - South <= 0.5; // keep imports town-sized

    public override string ToString() => $"{Name} [{West},{South} → {East},{North}]";
}
