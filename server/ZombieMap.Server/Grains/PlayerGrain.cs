using ZombieMap.Core.Encounters;
using ZombieMap.Core.Health;
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
    [Id(5)] public BodyState? Body { get; set; }
    /// <summary>Medical supplies the active encounter started with (to know what it used up).</summary>
    [Id(6)] public Supplies? EncounterSupplies { get; set; }
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
        var body = Body();
        RequireAlive();
        // Phase 0: starting a new encounter abandons the old one. Later: resume it instead.
        state.State.EncountersStarted++;
        string encounterId = $"{this.GetPrimaryKeyString()}/{state.State.EncountersStarted}";

        // You fight with the body, clothes and medical supplies you actually have.
        var supplies = BodyRules.SuppliesFrom(Inv);
        state.State.EncounterSupplies = supplies;
        var config = new EncounterConfig
        {
            Seed = (ulong)Random.Shared.NextInt64(),
            Arena = request.Arena,
            ZombieCount = Math.Clamp(request.ZombieCount, 1, MaxZombies),
            StartSupplies = supplies,
            StartBlood = body.Blood,
            StartEndurance = body.Endurance,
            StartWounds = body.Wounds,
            Worn = Inv.Worn,
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
            // Bring the fight home: wounds, blood, endurance, supplies used, clothes worn down.
            var survivor = await GrainFactory.GetGrain<IEncounterGrain>(encounterId).Outcome(this.GetPrimaryKeyString());
            var body = Body();
            BodyRules.ApplyEncounter(body, survivor, result.View.Status == EncounterStatus.Died, result.View.DeathCause, Now());
            if (state.State.EncounterSupplies != null) BodyRules.ConsumeUsed(Inv, state.State.EncounterSupplies, survivor.Supplies);
            foreach (var worn in Inv.Worn)
            {
                var after = survivor.Worn.Find(w => w.Id == worn.Id);
                if (after != null) worn.Condition = after.Condition;
            }
            state.State.EncounterSupplies = null;
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
        // Dressed, but only just: a T-shirt and jeans barely stop anything.
        inv.Worn.Add(new ItemStack { Id = inv.NewId(), ItemId = "clothing.tshirt" });
        inv.Worn.Add(new ItemStack { Id = inv.NewId(), ItemId = "clothing.jeans" });
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
        RequireAlive();
        var reply = await Loot(buildingId).Search(containerId);
        reply.Inventory = Inv;
        return reply;
    }

    public async Task<LootMessage> Take(string buildingId, string containerId, string stackId, int count, string to)
    {
        RequireAlive();
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

    // ------------------------------------------------------------------ health and clothing

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>The body, moved forward to now (bleeding, infection, healing since the last request).</summary>
    private BodyState Body()
    {
        state.State.Body ??= new BodyState { LastUpdated = Now() };
        BodyRules.Advance(state.State.Body, Now());
        return state.State.Body;
    }

    private void RequireAlive()
    {
        var body = Body();
        if (body.Dead) throw new GameRuleException("dead", $"You're dead ({body.DeathCause}). Start a new survivor.");
    }

    private HealthMessage Health()
    {
        long now = Now();
        return new HealthMessage { Body = BodyRules.ToView(Body(), now), Inventory = Inv, Now = now };
    }

    public Task<HealthMessage> GetHealth() => Task.FromResult(Health());

    public async Task<HealthMessage> Treat(int woundId, TreatAction action)
    {
        if (state.State.ActiveEncounterId != null) throw new GameRuleException("in_encounter", "Finish the fight first.");
        var error = BodyRules.Treat(Body(), Inv, woundId, action, Now());
        if (error != null) throw new GameRuleException("treat", error);
        await state.WriteStateAsync();
        return Health();
    }

    public async Task<HealthMessage> TakeMedicine(string stackId)
    {
        var error = BodyRules.TakeMedicine(Body(), Inv, stackId, Now());
        if (error != null) throw new GameRuleException("medicine", error);
        await state.WriteStateAsync();
        return Health();
    }

    public async Task<HealthMessage> Wear(string stackId)
    {
        if (state.State.ActiveEncounterId != null) throw new GameRuleException("in_encounter", "No time to change clothes.");
        Check(InventoryRules.Wear(Inv, stackId));
        await state.WriteStateAsync();
        return Health();
    }

    public async Task<HealthMessage> TakeOff(string stackId)
    {
        if (state.State.ActiveEncounterId != null) throw new GameRuleException("in_encounter", "No time to change clothes.");
        Check(InventoryRules.TakeOff(Inv, stackId));
        await state.WriteStateAsync();
        return Health();
    }

    public async Task<HealthMessage> NewSurvivor()
    {
        // Placeholder until we decide how harsh death is: everything you carried is gone.
        if (!Body().Dead) throw new GameRuleException("not_dead", "You're still alive.");
        state.State.Body = new BodyState { LastUpdated = Now() };
        state.State.Inventory = StarterInventory();
        state.State.ActiveEncounterId = null;
        state.State.EncounterSupplies = null;
        await state.WriteStateAsync();
        return Health();
    }

    private static GameRuleException Missing() => new("no_such_item", "You don't have that.");

    private static void Check(string? error)
    {
        if (error != null) throw new GameRuleException("inventory", error);
    }
}
