using ZombieMap.Core.Buildings;

namespace ZombieMap.Server.Grains;

[GenerateSerializer]
public sealed class BuildingState
{
    [Id(0)] public FloorPlan? Plan { get; set; }
}

/// <summary>
/// One per real-world building. Generates the interior once, then serves the cached plan,
/// so every player sees the same rooms. Later: shared loot state, ownership, upkeep.
/// </summary>
public sealed class BuildingGrain(
    [PersistentState("building")] IPersistentState<BuildingState> state) : Grain, IBuildingGrain
{
    public async Task<FloorPlan> GetFloorPlan(BuildingFootprint footprint)
    {
        var cached = state.State.Plan;
        if (cached != null && cached.GeneratorVersion == FloorPlanGenerator.Version)
            return cached;

        // Phase 0: the footprint comes from the client and the first request wins.
        // Phase 0 building-data spike: load it from our own Overture/OSM table by ID instead.
        footprint.BuildingId = this.GetPrimaryKeyString();
        var plan = FloorPlanGenerator.Generate(footprint);
        state.State.Plan = plan;
        await state.WriteStateAsync();
        return plan;
    }
}
