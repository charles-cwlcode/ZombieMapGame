using Xunit;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Zombies;

namespace ZombieMap.Core.Tests;

public class ZombieTests
{
    private const long T0 = 1_800_000_000;
    private const string Cell = "89195da49b7ffff";
    private static readonly LatLng Centre = new(52.2, -0.27);

    /// <summary>A hexagon ~350 m across, like an H3 resolution-9 cell.</summary>
    private static readonly List<LatLng> Hex = Enumerable.Range(0, 6)
        .Select(i => GeoMath.Offset(Centre, 200 * Math.Cos(i * Math.PI / 3), 200 * Math.Sin(i * Math.PI / 3)))
        .ToList();

    /// <summary>A square building, metres east/north of the centre.</summary>
    private static List<LatLng> Box(double x0, double y0, double x1, double y1) => new()
    {
        GeoMath.Offset(Centre, x0, y0), GeoMath.Offset(Centre, x1, y0), GeoMath.Offset(Centre, x1, y1), GeoMath.Offset(Centre, x0, y1),
    };

    private static ZombieCellState Seeded(CellBuildings b)
    {
        var s = new ZombieCellState();
        ZombieRules.Seed(s, Cell, b, T0);
        return s;
    }

    [Fact]
    public void BusierPlacesHaveMoreZombies()
    {
        int field = ZombieRules.BaseCount(Cell, new CellBuildings());
        int estate = ZombieRules.BaseCount(Cell, new CellBuildings { Houses = 150 });
        int centre = ZombieRules.BaseCount(Cell, new CellBuildings { Houses = 60, Shops = 25, Pharmacies = 2, Offices = 10 });
        Assert.InRange(field, 0, 2);
        Assert.InRange(estate, 6, 14);
        Assert.True(centre > estate, $"centre {centre} vs estate {estate}");
        Assert.Equal(ZombieRules.MaxPerCell, ZombieRules.BaseCount(Cell, new CellBuildings { Stations = 20 }));
    }

    [Fact]
    public void ACellsZombiesComeInFixedGroupsOfOneKind()
    {
        var groups = ZombieRules.Groups(Cell, 37);
        Assert.Equal(37, groups.Sum(g => g.Size));
        Assert.All(groups, g => Assert.InRange(g.Size, 1, 12));
        Assert.All(groups.Where(g => g.Kind == ZombieKind.Runner), g => Assert.True(g.Size <= 6));
        Assert.Equal(groups.Select(g => (g.Size, g.Kind)), ZombieRules.Groups(Cell, 37).Select(g => (g.Size, g.Kind)));
        Assert.True(groups.Count > 3, "split into several groups");

        // Across many cells there are all three kinds, mostly shamblers.
        var kinds = Enumerable.Range(0, 300).SelectMany(i => ZombieRules.Groups("cell" + i, 10)).GroupBy(g => g.Kind).ToDictionary(k => k.Key, k => k.Count());
        Assert.True(kinds[ZombieKind.Shambler] > kinds[ZombieKind.Runner] && kinds[ZombieKind.Runner] > 0 && kinds[ZombieKind.Crawler] > 0);
    }

    [Fact]
    public void KilledZombiesStayDeadThenTheCellRefills()
    {
        var s = Seeded(new CellBuildings { Houses = 150 });
        var groups = ZombieRules.Groups(Cell, s.Base);
        var biggest = groups.OrderByDescending(g => g.Size).First();
        Assert.Equal(2, ZombieRules.Kill(s, groups, biggest.Index, 2));
        Assert.Equal(biggest.Size - 2, ZombieRules.Alive(s, groups).GetValueOrDefault(biggest.Index));
        Assert.Equal(biggest.Size - 2, ZombieRules.Kill(s, groups, biggest.Index, 99)); // can't kill more than are left
        Assert.False(ZombieRules.Alive(s, groups).ContainsKey(biggest.Index));

        ZombieRules.Advance(s, T0 + ZombieRules.RegrowSeconds + 1);
        Assert.Equal(1, ZombieRules.Alive(s, groups)[biggest.Index]);
        ZombieRules.Advance(s, T0 + 100 * ZombieRules.RegrowSeconds);
        Assert.Equal(biggest.Size, ZombieRules.Alive(s, groups)[biggest.Index]);
    }

    [Fact]
    public void PathsGoAroundBuildings()
    {
        var grid = new ObstacleGrid(Centre, 300);
        grid.AddPolygon(Box(-20, -40, 20, 40)); // a long building in the way
        Assert.True(grid.BlockedCount > 400);
        var from = GeoMath.Offset(Centre, -60, 0);
        var to = GeoMath.Offset(Centre, 60, 0);
        Assert.False(grid.LineClear(from, to));

        var path = grid.FindPath(from, to);
        Assert.NotNull(path);
        Assert.True(path!.Count >= 3, "turns a corner");
        for (int i = 1; i < path.Count; i++) Assert.True(grid.LineClear(path[i - 1], path[i]), $"leg {i} goes through the building");
        double length = ZombieMotion.PathLength(path);
        Assert.InRange(length, 125, 200); // round the end of a 80 m building, not wildly further

        // A target inside a building: it gets as close as it can.
        var inside = grid.FindPath(from, Centre);
        Assert.NotNull(inside);
        Assert.True(grid.IsFree(inside![^1]));
    }

    [Fact]
    public void PatrolLoopsStayOutOfBuildings()
    {
        var grid = new ObstacleGrid(Centre, 300);
        for (int x = -180; x < 180; x += 30)
            for (int y = -180; y < 180; y += 30)
                grid.AddPolygon(Box(x, y, x + 18, y + 18)); // a grid of houses
        for (int g = 0; g < 25; g++)
        {
            var p = ZombieRules.PatrolFor(Cell, g, Hex, grid);
            Assert.All(p.Loop, pt => Assert.True(grid.IsFree(pt)));
            for (int i = 0; i < p.Loop.Count; i++)
                Assert.True(grid.LineClear(p.Loop[i], p.Loop[(i + 1) % p.Loop.Count]));
            for (long t = 0; t < 120_000; t += 7_000)
                Assert.True(grid.IsFree(ZombieMotion.PatrolAt(p, T0 * 1000 + t)), "walks through a house");
        }
    }

    [Fact]
    public void AChasingGroupRunsAtYouThenWalksHome()
    {
        var grid = new ObstacleGrid(Centre, 300);
        var patrol = ZombieRules.PatrolFor(Cell, 0, Hex, grid);
        var motion = ZombieRules.MotionFor(Cell, 0, patrol, null);
        long now = T0 * 1000;
        var start = motion.PositionAt(now);
        var player = GeoMath.Offset(start, 20, 0);

        var trip = ZombieRules.Chase(motion, grid, player, now, "p1")!;
        Assert.Equal("p1", trip.Chasing);
        var chasing = ZombieRules.MotionFor(Cell, 0, patrol, trip);
        Assert.Equal(start.Lat, chasing.PositionAt(now).Lat, 9);
        double speed = ZombieSpeeds.Chase(chasing.Kind);
        var later = chasing.PositionAt(now + 4000);
        Assert.InRange(GeoMath.Distance(start, later), Math.Min(20, speed * 4) - 1.5, Math.Min(20, speed * 4) + 1.5);
        Assert.True(chasing.Busy(now + 4000));

        long back = trip.PatrolEpochMs;
        Assert.False(chasing.Busy(back + 1));
        Assert.True(GeoMath.Distance(chasing.PositionAt(back), patrol.Loop[0]) < 0.5, "back on its loop where it starts");
    }

    [Fact]
    public void RunnersAreFasterThanYouCrawlersAreNot()
    {
        Assert.True(ZombieSpeeds.Chase(ZombieKind.Runner) > 1.4); // faster than walking
        Assert.True(ZombieSpeeds.Chase(ZombieKind.Shambler) < 1.4);
        Assert.True(ZombieSpeeds.Chase(ZombieKind.Crawler) < ZombieSpeeds.Chase(ZombieKind.Shambler));
        Assert.True(ZombieSpeeds.Notice(ZombieKind.Runner) > ZombieSpeeds.Notice(ZombieKind.Crawler));
    }

    [Fact]
    public void FightsUseTheKindsAndDirectionsFromTheMap()
    {
        var kinds = new List<ZombieKind> { ZombieKind.Runner, ZombieKind.Runner, ZombieKind.Crawler };
        var e = Encounter.Create(new EncounterConfig { Seed = 9, Kinds = kinds, FromLanes = new List<int> { 2, 2, 6 } });
        Assert.Equal(3, e.Zombies.Count);
        Assert.Equal(kinds, e.Zombies.Select(z => z.Kind));
        Assert.Equal(new[] { 2, 2, 6 }, e.Zombies.Select(z => z.Lane));

        // In an alley only N and S are open: an east-side zombie comes from the nearest open lane.
        var alley = Encounter.Create(new EncounterConfig { Seed = 9, Arena = ArenaKind.Street, Kinds = new List<ZombieKind> { ZombieKind.Shambler }, FromLanes = new List<int> { 1 } });
        Assert.Equal(0, alley.Zombies[0].Lane);
    }

    [Fact]
    public void MarkerIdsRoundTrip()
    {
        Assert.True(ZombieRules.TryParseMarker(ZombieRules.MarkerId(Cell, 12), out var cell, out int index));
        Assert.Equal(Cell, cell);
        Assert.Equal(12, index);
        Assert.False(ZombieRules.TryParseMarker("nope", out _, out _));

        string horde = HordeRules.HordeId("87195da49ffffff", 1);
        Assert.False(ZombieRules.TryParseMarker(HordeRules.MarkerId(horde), out _, out _));
        Assert.True(HordeRules.TryParseMarker(HordeRules.MarkerId(horde), out var h));
        Assert.Equal(horde, h);
        Assert.InRange(HordeRules.SizeOf(horde), HordeRules.MinSize, HordeRules.MaxSize);
    }

    [Fact]
    public void ArenaFollowsHowBuiltUpTheCellIs()
    {
        Assert.Equal(ArenaKind.Field, ZombieRules.ArenaFor(new CellBuildings { Houses = 3 }));
        Assert.Equal(ArenaKind.CarPark, ZombieRules.ArenaFor(new CellBuildings { Houses = 30 }));
        Assert.Equal(ArenaKind.Street, ZombieRules.ArenaFor(new CellBuildings { Houses = 200 }));
    }
}
