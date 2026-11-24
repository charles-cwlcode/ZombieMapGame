using ZombieMap.Core.Buildings;
using ZombieMap.Server.World;

namespace ZombieMap.Server.Grains;

[GenerateSerializer]
public sealed class BuildingState
{
    [Id(0)] public FloorPlan? Plan { get; set; }
}

/// <summary>
/// One per real-world building, keyed by its stable Overture ID. Generates the interior once from
/// the server's own footprint, then serves the cached plan, so every player sees the same rooms.
/// Later: shared loot state for special buildings, ownership, upkeep.
/// </summary>
public sealed class BuildingGrain(
    [PersistentState("building")] IPersistentState<BuildingState> state,
    WorldStore world) : Grain, IBuildingGrain
{
    public async Task<FloorPlan> GetFloorPlan(BuildingFootprint? devFootprint)
    {
        var cached = state.State.Plan;
        if (cached != null && cached.GeneratorVersion == FloorPlanGenerator.Version)
            return cached;

        string id = this.GetPrimaryKeyString();
        var footprint = await LoadFootprint(id, devFootprint)
            ?? throw new GameRuleException("unknown_building", "There's no building with that ID.");

        footprint.BuildingId = id;
        var plan = FloorPlanGenerator.Generate(footprint);
        state.State.Plan = plan;
        await state.WriteStateAsync();
        return plan;
    }

    private async Task<BuildingFootprint?> LoadFootprint(string id, BuildingFootprint? devFootprint)
    {
        // Real buildings: the footprint comes from our map data.
        if (devFootprint == null) return await world.GetFootprintAsync(id);

        // Hand-made dev buildings also work when the map database is offline.
        try
        {
            // A real ID always uses our data, so a client can't change a real building's shape.
            return await world.GetFootprintAsync(id) ?? devFootprint;
        }
        catch (GameRuleException ex) when (ex.Code is "world_offline" or "world_loading")
        {
            return devFootprint;
        }
    }
}
