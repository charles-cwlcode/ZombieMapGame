using ZombieMap.Core.Buildings;
using ZombieMap.Core.Items;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Server.Grains;

[GenerateSerializer]
public sealed class LootContainerState
{
    [Id(0)] public DateTime? SearchStartedUtc { get; set; }

    /// <summary>Null until the search finishes; then what's in it (changes as the player takes and puts).</summary>
    [Id(1)] public ItemContainer? Contents { get; set; }
}

[GenerateSerializer]
public sealed class LootState
{
    [Id(0)] public Dictionary<string, LootContainerState> Containers { get; set; } = new();

    /// <summary>IDs for items put into containers ("p1", "p2"…), so they never clash with generated ones.</summary>
    [Id(1)] public int NextId { get; set; }
}

/// <summary>
/// One player's copy of one normal building's loot, keyed "{playerId}|{buildingId}". Contents are
/// generated the first time each container's search finishes, then kept (taking and putting back
/// change them). Only that player's PlayerGrain calls it. Later: respawn after a while; special
/// buildings get one shared copy instead.
/// </summary>
public sealed class LootGrain(
    [PersistentState("loot")] IPersistentState<LootState> state) : Grain, ILootGrain
{
    /// <summary>Network and timer slack: a search may finish this much early.</summary>
    private const double SearchToleranceSeconds = 0.3;

    private string PlayerId => this.GetPrimaryKeyString().Split('|', 2)[0];
    private string BuildingId => this.GetPrimaryKeyString().Split('|', 2)[1];

    public async Task<LootMessage> Search(string containerId)
    {
        var (plan, room, container) = await FindContainer(containerId);
        var entry = Entry(containerId);
        var reply = new LootMessage { BuildingId = BuildingId, ContainerId = containerId };

        if (entry.Contents != null)
        {
            reply.Status = SearchStatus.Done;
            reply.Contents = entry.Contents;
            return reply;
        }

        var now = DateTime.UtcNow;
        double needed = LootRules.SearchSeconds(container.Kind);
        if (entry.SearchStartedUtc == null)
        {
            entry.SearchStartedUtc = now;
            await state.WriteStateAsync();
            reply.Status = SearchStatus.Searching;
            reply.SecondsLeft = needed;
            return reply;
        }

        double left = needed - (now - entry.SearchStartedUtc.Value).TotalSeconds;
        if (left > SearchToleranceSeconds)
        {
            reply.Status = SearchStatus.Searching;
            reply.SecondsLeft = Math.Round(left, 2);
            return reply;
        }

        entry.Contents = LootGenerator.Generate($"{PlayerId}|{BuildingId}|{containerId}|0", plan.Kind, room.Type, container,
            LootGenerator.UnixDay(now));
        await state.WriteStateAsync();
        reply.Status = SearchStatus.Done;
        reply.Contents = entry.Contents;
        return reply;
    }

    public Task<ItemContainer> Get(string containerId) => Task.FromResult(Searched(containerId));

    public async Task<ItemStack> Remove(string containerId, string stackId, int count)
    {
        var contents = Searched(containerId);
        var taken = InventoryRules.Take(contents, stackId, count, NewId)
            ?? throw new GameRuleException("no_such_item", "That item isn't there any more.");
        await state.WriteStateAsync();
        return taken;
    }

    public async Task Add(string containerId, ItemStack stack)
    {
        var contents = Searched(containerId);
        var hand = new ItemContainer { Id = "hand", Capacity = double.MaxValue, Items = { stack } };
        string? error = InventoryRules.Move(hand, contents, stack.Id, 0, NewId, renameAll: true, out _);
        if (error != null) throw new GameRuleException("no_room", error);
        await state.WriteStateAsync();
    }

    private ItemContainer Searched(string containerId) =>
        state.State.Containers.TryGetValue(containerId, out var e) && e.Contents != null
            ? e.Contents
            : throw new GameRuleException("not_searched", "Search it first.");

    private LootContainerState Entry(string containerId)
    {
        if (!state.State.Containers.TryGetValue(containerId, out var entry))
            state.State.Containers[containerId] = entry = new LootContainerState();
        return entry;
    }

    private string NewId() => "p" + (++state.State.NextId);

    private async Task<(FloorPlan plan, Room room, Container container)> FindContainer(string containerId)
    {
        // The building's plan says which containers exist and what room they're in.
        var plan = await GrainFactory.GetGrain<IBuildingGrain>(BuildingId).GetFloorPlan(null);
        foreach (var room in plan.Rooms)
            foreach (var c in room.Containers)
                if (c.Id == containerId) return (plan, room, c);
        throw new GameRuleException("no_such_container", "There's no such container in this building.");
    }
}
