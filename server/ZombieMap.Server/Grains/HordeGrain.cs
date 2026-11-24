using Orleans.Concurrency;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Rng;
using StableHash = ZombieMap.Core.Rng.StableHash;
using ZombieMap.Core.Zombies;
using ZombieMap.Server.World;

namespace ZombieMap.Server.Grains;

[GenerateSerializer]
public sealed class HordeReport
{
    [Id(0)] public ZombieMarker? Marker { get; set; }
}

/// <summary>
/// One roaming horde (key "{region}:{n}"). Walks from cell to cell inside its region, around buildings
/// (paths come from the cell it's in), resting at each stop. Its route is planned half an hour ahead,
/// so it moves whether or not anyone is watching, and every player sees it in the same place.
/// </summary>
public interface IHordeGrain : IGrainWithStringKey
{
    Task<HordeReport> Get(long nowMs);
    Task<int> Kill(int count);
    /// <summary>A loud noise: if it's in earshot the horde heads there.</summary>
    [OneWay] Task Hear(LatLng at, long nowMs);
    /// <summary>A player close by: the horde lurches toward them.</summary>
    [OneWay] Task Notice(LatLng player, long nowMs);
}

public sealed class HordeGrain(
    [PersistentState("horde")] IPersistentState<HordeState> state) : Grain, IHordeGrain
{
    private string Id => this.GetPrimaryKeyString();
    private string Region => Id[..Id.LastIndexOf(':')];

    public async Task<HordeReport> Get(long nowMs)
    {
        await Plan(nowMs);
        var s = state.State;
        if (HordeRules.BrokenUp(s)) return new HordeReport();
        var motion = HordeRules.Motion(s);
        return new HordeReport
        {
            Marker = new ZombieMarker
            {
                Id = HordeRules.MarkerId(Id),
                Kind = motion.Kind,
                Count = HordeRules.Alive(s),
                Horde = true,
                Position = motion.PositionAt(nowMs),
                Motion = motion,
            },
        };
    }

    public async Task<int> Kill(int count)
    {
        var s = state.State;
        int n = Math.Min(count, HordeRules.Alive(s));
        s.Dead += n;
        await state.WriteStateAsync();
        return n;
    }

    public async Task Hear(LatLng at, long nowMs)
    {
        await Plan(nowMs);
        var here = HordeRules.Motion(state.State).PositionAt(nowMs);
        if (GeoMath.Distance(here, at) > HordeRules.HearMetres) return;
        await Divert(here, at, nowMs, HordeRules.Speed * 1.3, 5 * 60 * 1000);
    }

    public async Task Notice(LatLng player, long nowMs)
    {
        await Plan(nowMs);
        var here = HordeRules.Motion(state.State).PositionAt(nowMs);
        if (GeoMath.Distance(here, player) > HordeRules.NoticeMetres) return;
        // Already lurching toward this spot? Leave it be.
        var current = state.State.Legs.LastOrDefault(l => l.StartMs <= nowMs && nowMs < l.EndMs);
        if (current != null && GeoMath.Distance(current.Path[^1], player) < 5) return;
        await Divert(here, player, nowMs, HordeRules.Speed * 1.6, 20 * 1000);
    }

    /// <summary>Drop the rest of the route and head somewhere, then rest there and carry on roaming.</summary>
    private async Task Divert(LatLng here, LatLng to, long nowMs, double speed, long holdMs)
    {
        var s = state.State;
        s.Legs.RemoveAll(l => l.StartMs > nowMs);
        foreach (var l in s.Legs) l.HoldUntilMs = Math.Min(l.HoldUntilMs, nowMs); // stop resting
        // A leg cut short: the horde is where it is now.
        s.Legs.RemoveAll(l => l.EndMs > nowMs);
        var path = await GrainFactory.GetGrain<IZombieCellGrain>(ZombieGrid.CellAt(here)).FindPath(here, to)
                   ?? new List<LatLng> { here, to };
        var leg = new MotionLeg { Kind = LegKind.Horde, Path = path, StartMs = nowMs, Speed = speed };
        leg.HoldUntilMs = leg.ArriveMs + holdMs;
        s.Legs.Add(leg);
        s.PreviousCell = s.Cell;
        s.Cell = ZombieGrid.CellAt(to);
        s.RestingAt = path[^1];
        await state.WriteStateAsync();
    }

    /// <summary>Makes sure the route covers the next half hour, walking on from where the horde is.</summary>
    private async Task Plan(long nowMs)
    {
        var s = state.State;
        bool changed = false;
        if (!s.Seeded)
        {
            var cells = ZombieGrid.CellsIn(Region);
            var rng = new Pcg32(StableHash.Hash64("hordestart|" + Id));
            s.Seeded = true;
            s.Size = HordeRules.SizeOf(Id);
            s.Cell = cells[rng.Range(0, cells.Count)];
            s.RestingAt = ZombieGrid.Centre(s.Cell);
            changed = true;
        }
        if (HordeRules.BrokenUp(s)) return;

        // Forget legs that ended a while ago (the last one says where it's resting).
        int before = s.Legs.Count;
        s.Legs.RemoveAll(l => l.EndMs < nowMs - 60_000 && l != s.Legs[^1]);
        if (s.Legs.Count > 0 && s.Legs[^1].EndMs < nowMs - 60_000)
        {
            s.RestingAt = s.Legs[^1].Path[^1];
            s.Legs.Clear();
        }
        changed |= s.Legs.Count != before;

        long until = nowMs + HordeRules.PlanAheadMs;
        int guard = 0;
        while ((s.Legs.Count == 0 ? nowMs : s.Legs[^1].EndMs) < until && guard++ < 12)
        {
            long start = s.Legs.Count == 0 ? nowMs : s.Legs[^1].EndMs;
            var from = s.Legs.Count == 0 ? s.RestingAt : s.Legs[^1].Path[^1];
            var rng = new Pcg32(StableHash.Hash64($"hordestep|{Id}|{s.Steps++}"));
            var options = ZombieGrid.Neighbours(s.Cell!).Where(c => ZombieGrid.RegionOf(c) == Region && c != s.PreviousCell).ToList();
            if (options.Count == 0) options = ZombieGrid.Neighbours(s.Cell!).Where(c => ZombieGrid.RegionOf(c) == Region).ToList();
            if (options.Count == 0) options = ZombieGrid.CellsIn(Region);
            string next = options[rng.Range(0, options.Count)];
            var target = GeoMath.Offset(ZombieGrid.Centre(next), rng.Range(-40, 40), rng.Range(-40, 40));
            var path = await GrainFactory.GetGrain<IZombieCellGrain>(ZombieGrid.CellAt(from)).FindPath(from, target)
                       ?? new List<LatLng> { from, target };
            var leg = new MotionLeg { Kind = LegKind.Horde, Path = path, StartMs = start, Speed = HordeRules.Speed };
            leg.HoldUntilMs = leg.ArriveMs + (long)rng.Range(HordeRules.RestMinMs, HordeRules.RestMaxMs);
            s.Legs.Add(leg);
            s.PreviousCell = s.Cell;
            s.Cell = next;
            s.RestingAt = path[^1];
            changed = true;
        }
        if (changed) await state.WriteStateAsync();
    }
}
