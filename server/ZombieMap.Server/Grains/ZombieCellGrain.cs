using Orleans.Concurrency;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Rng;
using StableHash = ZombieMap.Core.Rng.StableHash;
using ZombieMap.Core.Zombies;
using ZombieMap.Server.World;

namespace ZombieMap.Server.Grains;

/// <summary>What a cell tells a player: its groups (with how they move), noise and what it's built of.</summary>
[GenerateSerializer]
public sealed class ZombieCellReport
{
    [Id(0)] public List<ZombieMarker> Groups { get; set; } = new();
    [Id(1)] public double Noise { get; set; }
    [Id(2)] public CellBuildings Buildings { get; set; } = new();
    [Id(3)] public int Alive { get; set; }
}

/// <summary>
/// One H3 cell's zombies (key: the cell's H3 index), in groups. Shared by every player and saved.
/// Keeps a grid of the buildings around it in memory, so its groups (and passing hordes) walk around
/// them. Calls to neighbours and hordes are one way (no waiting on a reply), so grains never wait on
/// each other in a circle.
/// </summary>
public interface IZombieCellGrain : IGrainWithStringKey
{
    /// <summary>A player is here: groups that notice them give chase; ones that lost them go home.</summary>
    Task<ZombieCellReport> React(string playerId, LatLng player, long nowMs, bool notice);
    /// <summary>Kills up to <paramref name="count"/> zombies of a group. Returns how many died.</summary>
    Task<int> Kill(int group, int count);
    /// <summary>A noise here: groups in this and the neighbouring cells, and nearby hordes, head for it.</summary>
    Task MakeNoise(double amount, LatLng at);
    /// <summary>A walking route around buildings (for hordes passing through), or null.</summary>
    Task<List<LatLng>?> FindPath(LatLng from, LatLng to);

    [OneWay] Task Hear(LatLng at, double noise, long nowMs);
}

public sealed class ZombieCellGrain(
    [PersistentState("zombies")] IPersistentState<ZombieCellState> state,
    WorldStore world,
    ILogger<ZombieCellGrain> log) : Grain, IZombieCellGrain
{
    private string Cell => this.GetPrimaryKeyString();
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // Worked out from the cell and the map, not saved.
    private ObstacleGrid? _grid;
    private bool _gridHasMap;
    private List<LatLng>? _outline;
    private List<ZombieGroup>? _groups;
    private readonly Dictionary<int, Patrol> _patrols = new();

    public async Task<ZombieCellReport> React(string playerId, LatLng player, long nowMs, bool notice)
    {
        await Ready(nowMs);
        var s = state.State;
        bool changed = false;
        var report = new ZombieCellReport { Noise = Math.Round(s.Noise, 2), Buildings = s.Buildings, Alive = ZombieRules.AliveTotal(s) };
        foreach (var (group, alive) in ZombieRules.Alive(s, _groups!).OrderBy(kv => kv.Key))
        {
            var motion = Motion(group);
            var here = motion.PositionAt(nowMs);
            double distance = GeoMath.Distance(here, player);
            s.Trips.TryGetValue(group, out var trip);
            bool busy = motion.Busy(nowMs);

            if (trip != null && busy && trip.Chasing == playerId)
            {
                if (!notice || distance > ZombieRules.LoseMetres)
                    changed |= SetTrip(group, ZombieRules.GoHome(motion, _grid!, nowMs));
                else if (GeoMath.Distance(ChaseTarget(trip), player) > 3)
                    changed |= SetTrip(group, ZombieRules.Chase(motion, _grid!, player, nowMs, playerId));
            }
            else if (notice && (!busy || trip?.Chasing == null) && distance <= ZombieSpeeds.Notice(motion.Kind) && _grid!.LineClear(here, player))
            {
                changed |= SetTrip(group, ZombieRules.Chase(motion, _grid!, player, nowMs, playerId));
            }

            motion = Motion(group);
            s.Trips.TryGetValue(group, out trip);
            report.Groups.Add(new ZombieMarker
            {
                Id = ZombieRules.MarkerId(Cell, group),
                Kind = motion.Kind,
                Count = alive,
                Position = motion.PositionAt(nowMs),
                Motion = Public(motion),
                Chasing = trip?.Chasing == playerId && motion.Busy(nowMs),
            });
        }
        if (changed) await state.WriteStateAsync();
        return report;
    }

    public async Task<int> Kill(int group, int count)
    {
        await Ready(NowMs());
        int killed = ZombieRules.Kill(state.State, _groups!, group, count);
        if (killed > 0) await state.WriteStateAsync();
        return killed;
    }

    public async Task MakeNoise(double amount, LatLng at)
    {
        long now = NowMs();
        await Ready(now);
        state.State.Noise += amount;
        await state.WriteStateAsync();
        double noise = state.State.Noise;
        foreach (var (cell, _) in ZombieGrid.Disk(Cell, 1))
            _ = GrainFactory.GetGrain<IZombieCellGrain>(cell).Hear(at, noise, now);
        foreach (var region in ZombieGrid.RegionsAround(Cell))
            for (int n = 0; n < HordeRules.HordesIn(region); n++)
                _ = GrainFactory.GetGrain<IHordeGrain>(HordeRules.HordeId(region, n)).Hear(at, now);
    }

    /// <summary>Some of the idle groups within earshot (nearest first) head for the noise.</summary>
    public async Task Hear(LatLng at, double noise, long nowMs)
    {
        await Ready(nowMs);
        var s = state.State;
        var idle = new List<(int group, ZombieMotion motion, double distance)>();
        foreach (var group in ZombieRules.Alive(s, _groups!).Keys)
        {
            var m = Motion(group);
            if (m.Busy(nowMs)) continue;
            double d = GeoMath.Distance(m.PositionAt(nowMs), at);
            if (d <= ZombieRules.HearMetres) idle.Add((group, m, d));
        }
        int n = ZombieRules.DrawnBy(noise, idle.Count);
        if (n == 0) return;
        var rng = new Pcg32(StableHash.Combine(StableHash.Hash64(Cell), (ulong)nowMs));
        bool changed = false;
        foreach (var (group, motion, _) in idle.OrderBy(i => i.distance).Take(n))
            changed |= SetTrip(group, ZombieRules.Drift(motion, _grid!, at, nowMs, rng));
        if (changed) await state.WriteStateAsync();
    }

    public async Task<List<LatLng>?> FindPath(LatLng from, LatLng to)
    {
        await Ready(NowMs());
        return _grid!.FindPath(from, to);
    }

    // ------------------------------------------------------------------ helpers

    private ZombieMotion Motion(int group)
    {
        state.State.Trips.TryGetValue(group, out var trip);
        return ZombieRules.MotionFor(Cell, group, _patrols[group], trip);
    }

    private bool SetTrip(int group, ZombieTrip? trip)
    {
        if (trip == null) return false; // no way through: carry on as before
        state.State.Trips[group] = trip;
        return true;
    }

    private static LatLng ChaseTarget(ZombieTrip trip)
    {
        var chase = trip.Legs.FirstOrDefault(l => l.Kind == LegKind.Chase);
        return chase == null ? default : chase.Path[^1];
    }

    /// <summary>What the client may see of a motion: not who's being chased.</summary>
    private static ZombieMotion Public(ZombieMotion m) => new() { Kind = m.Kind, Patrol = m.Patrol, Legs = m.Legs };

    /// <summary>
    /// Brings the cell up to now. The first time, counts the real buildings in it to set its zombie
    /// count, and loads the buildings around it so its groups walk around them. While map data is still
    /// loading the cell uses a guess and open ground, and tries again later.
    /// </summary>
    private async Task Ready(long nowMs)
    {
        var s = state.State;
        long now = nowMs / 1000;
        bool mapReady = true;
        if (!s.Seeded)
        {
            CellBuildings buildings;
            try
            {
                buildings = await world.CountBuildingsAsync(ZombieGrid.Outline(Cell));
            }
            catch (GameRuleException ex) when (ex.Code is "world_loading" or "world_offline")
            {
                // No map data: a few houses, so dev servers without a database still have zombies.
                buildings = new CellBuildings { Houses = 40 };
                mapReady = ex.Code == "world_offline";
                log.LogDebug("Zombie cell {Cell} seeded without map data ({Code})", Cell, ex.Code);
            }
            ZombieRules.Seed(s, Cell, buildings, now);
            if (!mapReady) s.Seeded = false; // count again once the map is ready
            await state.WriteStateAsync();
        }
        else
        {
            ZombieRules.Advance(s, now);
        }

        if (_groups == null || _groups.Sum(g => g.Size) != s.Base)
        {
            _groups = ZombieRules.Groups(Cell, s.Base);
            _patrols.Clear();
        }
        if (_grid == null || (!_gridHasMap && mapReady && s.Seeded))
            await BuildGrid();
        foreach (var g in _groups)
            if (!_patrols.ContainsKey(g.Index))
                _patrols[g.Index] = ZombieRules.PatrolFor(Cell, g.Index, _outline!, _gridHasMap ? _grid : null);
    }

    private async Task BuildGrid()
    {
        _outline = ZombieGrid.Outline(Cell);
        var centre = ZombieGrid.Centre(Cell);
        var grid = new ObstacleGrid(centre, ZombieGrid.HalfSpanMetres(Cell, ZombieRules.GridMarginMetres));
        try
        {
            foreach (var building in await world.GetOutlinesNearAsync(centre, ZombieGrid.HalfSpanMetres(Cell, ZombieRules.GridMarginMetres)))
                grid.AddPolygon(building);
            _gridHasMap = true;
            _patrols.Clear(); // re-plan loops around the real buildings
        }
        catch (GameRuleException ex) when (ex.Code is "world_loading" or "world_offline")
        {
            _gridHasMap = false; // open ground for now
        }
        _grid = grid;
    }
}
