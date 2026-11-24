using ZombieMap.Core.Encounters;
using ZombieMap.Core.Items;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Server.Grains;

[GenerateSerializer]
public sealed class PlayerState
{
    [Id(0)] public string DisplayName { get; set; } = "Survivor";
    [Id(1)] public DateTime CreatedUtc { get; set; }
    [Id(2)] public string? ActiveEncounterId { get; set; }
    [Id(3)] public int EncountersStarted { get; set; }
    [Id(4)] public PlayerInventory? Inventory { get; set; }
}

public sealed class PlayerGrain(
    [PersistentState("player")] IPersistentState<PlayerState> state) : Grain, IPlayerGrain
{
    private const int MaxZombies = 12;

    public async Task<WelcomeMessage> Hello(string? displayName)
    {
        if (!state.RecordExists) state.State.CreatedUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(displayName))
            state.State.DisplayName = displayName.Trim()[..Math.Min(displayName.Trim().Length, 32)];
        await state.WriteStateAsync();

        return new WelcomeMessage
        {
            PlayerId = this.GetPrimaryKeyString(),
            DisplayName = state.State.DisplayName,
            ServerTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ActiveEncounterId = state.State.ActiveEncounterId,
        };
    }

    public async Task<EncounterStateMessage> StartEncounter(StartEncounterRequest request)
    {
        // Phase 0: starting a new encounter abandons the old one. Later: resume it instead.
        state.State.EncountersStarted++;
        string encounterId = $"{this.GetPrimaryKeyString()}/{state.State.EncountersStarted}";

        var config = new EncounterConfig
        {
            Seed = (ulong)Random.Shared.NextInt64(),
            Arena = request.Arena,
            ZombieCount = Math.Clamp(request.ZombieCount, 1, MaxZombies),
        };
        var result = await GrainFactory.GetGrain<IEncounterGrain>(encounterId).Start(this.GetPrimaryKeyString(), config);

        state.State.ActiveEncounterId = encounterId;
        await state.WriteStateAsync();
        return new EncounterStateMessage { EncounterId = encounterId, Result = result };
    }

    public async Task<EncounterStateMessage> Act(string encounterId, EncounterAction action)
    {
        if (state.State.ActiveEncounterId != encounterId)
            throw new GameRuleException("no_such_encounter", "That encounter isn't active.");

        var result = await GrainFactory.GetGrain<IEncounterGrain>(encounterId).Act(this.GetPrimaryKeyString(), action);

        if (result.View.Status != EncounterStatus.Ongoing)
        {
            // TODO(phase 1): copy wounds/supplies back to the player's persistent body + inventory.
            state.State.ActiveEncounterId = null;
            await state.WriteStateAsync();
        }
        return new EncounterStateMessage { EncounterId = encounterId, Result = result };
    }

    // ------------------------------------------------------------------ inventory

    /// <summary>What a new survivor has in their pockets.</summary>
    private static PlayerInventory StarterInventory()
    {
        var inv = new PlayerInventory();
        void Give(string itemId, int count = 1)
        {
            var def = ItemCatalog.Get(itemId);
            var s = new ItemStack { Id = inv.NewId(), ItemId = itemId, Count = count, UsesLeft = def.Uses };
            InventoryRules.InitBag(s);
            inv.Main.Items.Add(s);
        }
        Give("med.rag", 2);
        Give("med.bandage", 1);
        Give("drink.water_bottle");
        Give("food.chocolate");
        return inv;
    }

    private PlayerInventory Inv => state.State.Inventory ??= StarterInventory();

    public Task<PlayerInventory> GetInventory() => Task.FromResult(Inv);

    public async Task<PlayerInventory> Move(string stackId, int count, string to)
    {
        if (!Inv.TryFind(stackId, out var from, out _)) throw Missing();
        var target = Inv.Container(to) ?? throw new GameRuleException("bad_target", "Put it in your pockets or a bag on your back.");
        Check(InventoryRules.Move(from, target, stackId, count, Inv.NewId, renameAll: false, out _));
        await state.WriteStateAsync();
        return Inv;
    }

    public async Task<PlayerInventory> Equip(string stackId)
    {
        Check(InventoryRules.Equip(Inv, stackId));
        await state.WriteStateAsync();
        return Inv;
    }

    public async Task<PlayerInventory> Unequip()
    {
        Check(InventoryRules.Unequip(Inv));
        await state.WriteStateAsync();
        return Inv;
    }

    public async Task<PlayerInventory> Drop(string stackId, int count)
    {
        if (!Inv.TryFind(stackId, out var from, out _)) throw Missing();
        InventoryRules.Take(from, stackId, count, Inv.NewId);
        await state.WriteStateAsync();
        return Inv;
    }

    // ------------------------------------------------------------------ looting

    private ILootGrain Loot(string buildingId) => GrainFactory.GetGrain<ILootGrain>($"{this.GetPrimaryKeyString()}|{buildingId}");

    public async Task<LootMessage> Search(string buildingId, string containerId)
    {
        var reply = await Loot(buildingId).Search(containerId);
        reply.Inventory = Inv;
        return reply;
    }

    public async Task<LootMessage> Take(string buildingId, string containerId, string stackId, int count, string to)
    {
        var loot = Loot(buildingId);
        var target = Inv.Container(to) ?? throw new GameRuleException("bad_target", "Put it in your pockets or a bag on your back.");

        // Check it fits before taking it out of the container.
        var stack = (await loot.Get(containerId)).Find(stackId) ?? throw Missing();
        double weight = count <= 0 || count >= stack.Count ? InventoryRules.Weight(stack) : stack.Def().Weight * count;
        if (weight > InventoryRules.Free(target) + 1e-9)
            throw new GameRuleException("no_room", $"Not enough room: needs {weight:0.##} kg, {target.Name} has {InventoryRules.Free(target):0.##} kg free.");

        var taken = await loot.Remove(containerId, stackId, count);
        var hand = new ItemContainer { Id = "hand", Capacity = double.MaxValue, Items = { taken } };
        string? error = InventoryRules.Move(hand, target, taken.Id, 0, Inv.NewId, renameAll: true, out _);
        if (error != null)
        {
            await loot.Add(containerId, taken); // put it back
            throw new GameRuleException("no_room", error);
        }
        await state.WriteStateAsync();
        return new LootMessage
        {
            BuildingId = buildingId, ContainerId = containerId, Status = SearchStatus.Done,
            Contents = await loot.Get(containerId), Inventory = Inv,
        };
    }

    public async Task<LootMessage> Put(string buildingId, string containerId, string stackId, int count)
    {
        var loot = Loot(buildingId);
        await loot.Get(containerId); // must be searched
        if (!Inv.TryFind(stackId, out var from, out _)) throw Missing();

        var taken = InventoryRules.Take(from, stackId, count, Inv.NewId)!;
        try
        {
            await loot.Add(containerId, taken);
        }
        catch (GameRuleException)
        {
            var hand = new ItemContainer { Id = "hand", Capacity = double.MaxValue, Items = { taken } };
            InventoryRules.Move(hand, from, taken.Id, 0, Inv.NewId, renameAll: false, out _); // give it back
            throw;
        }
        await state.WriteStateAsync();
        return new LootMessage
        {
            BuildingId = buildingId, ContainerId = containerId, Status = SearchStatus.Done,
            Contents = await loot.Get(containerId), Inventory = Inv,
        };
    }

    private static GameRuleException Missing() => new("no_such_item", "You don't have that.");

    private static void Check(string? error)
    {
        if (error != null) throw new GameRuleException("inventory", error);
    }
}
