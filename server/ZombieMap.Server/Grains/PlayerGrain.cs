using ZombieMap.Core.Geo;
using ZombieMap.Core.Zombies;
using ZombieMap.Server.World;
using ZombieMap.Core.Buildings;
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
    /// <summary>Your safehouse and its stash. Kept when a survivor dies: it belongs to the line.</summary>
    [Id(7)] public SafehouseView? Safehouse { get; set; }
    /// <summary>Which survivor in the line this is (1 = the first).</summary>
    [Id(8)] public int SurvivorNumber { get; set; } = 1;
    /// <summary>Where the player last said they were (player.move).</summary>
    [Id(9)] public LatLng? LastPosition { get; set; }
    /// <summary>The map zombies ("cell:index", nearest first) in the active fight, if it started on the map.</summary>
    [Id(10)] public List<string>? FightZombies { get; set; }
    /// <summary>When the last fight on the map ended (unix seconds), for the cooldown.</summary>
    [Id(11)] public long MapFightEnded { get; set; }
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

    public Task<EncounterStateMessage> StartEncounter(StartEncounterRequest request) =>
        StartFight(request.Arena, Math.Clamp(request.ZombieCount, 1, MaxZombies), null, null);

    /// <summary>Starts a fight. From the map, <paramref name="kinds"/> and <paramref name="lanes"/> say who is coming and from where.</summary>
    private async Task<EncounterStateMessage> StartFight(ArenaKind arena, int count, List<ZombieKind>? kinds, List<int>? lanes)
    {
        var body = Body();
        RequireAlive();
        // Phase 0: starting a new encounter abandons the old one. Later: resume it instead.
        state.State.EncountersStarted++;
        state.State.FightZombies = null; // a dev-forced fight replaces a map fight: its zombies go back to the map
        string encounterId = $"{this.GetPrimaryKeyString()}/{state.State.EncountersStarted}";

        // You fight with the body, clothes and medical supplies you actually have.
        var supplies = BodyRules.SuppliesFrom(Inv);
        state.State.EncounterSupplies = supplies;
        var config = new EncounterConfig
        {
            Seed = (ulong)Random.Shared.NextInt64(),
            Arena = arena,
            ZombieCount = count,
            Kinds = kinds,
            FromLanes = lanes,
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
            await EndMapFight(result.View);
            await state.WriteStateAsync();
        }
        return new EncounterStateMessage { EncounterId = encounterId, Result = result };
    }

    // ------------------------------------------------------------------ zombies on the map

    /// <summary>Cells are looked at two rings out; groups within this distance are sent.</summary>
    private const int CellRings = 2;
    private const double ShowMetres = 450;

    /// <summary>
    /// The player is here now. Groups that see them give chase, hordes close by lurch over, and if a
    /// group is within <see cref="ZombieRules.EngageMetres"/> a fight starts: groups within
    /// <see cref="ZombieRules.JoinMetres"/> join, nearest first, up to <see cref="ZombieRules.MaxInFight"/>
    /// zombies, each coming from the direction it really is. The ones you kill are gone for everyone.
    /// </summary>
    public async Task<NearbyMessage> Move(LatLng position, bool allowFights)
    {
        state.State.LastPosition = position;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string me = this.GetPrimaryKeyString();
        bool notice = allowFights && CanBeNoticed();
        string here = ZombieGrid.CellAt(position);
        var disk = ZombieGrid.Disk(here, CellRings);
        var cellTask = Task.WhenAll(disk.Select(d => GrainFactory.GetGrain<IZombieCellGrain>(d.cell).React(me, position, nowMs, notice)));
        var hordeIds = ZombieGrid.RegionsAround(here)
            .SelectMany(r => Enumerable.Range(0, HordeRules.HordesIn(r)).Select(n => HordeRules.HordeId(r, n))).ToList();
        var hordeTask = Task.WhenAll(hordeIds.Select(id => GrainFactory.GetGrain<IHordeGrain>(id).Get(nowMs)));
        var reports = await cellTask;
        var hordes = await hordeTask;

        var reply = new NearbyMessage { Cell = here, TimeMs = nowMs };
        for (int i = 0; i < disk.Count; i++)
        {
            reply.Cells.Add(new ZombieCellView { Cell = disk[i].cell, Outline = ZombieGrid.Outline(disk[i].cell), Zombies = reports[i].Alive, Noise = reports[i].Noise });
            reply.Zombies.AddRange(reports[i].Groups.Where(g => GeoMath.Distance(position, g.Position) <= ShowMetres));
        }
        foreach (var h in hordes)
            if (h.Marker != null && GeoMath.Distance(position, h.Marker.Position) <= ShowMetres * 1.5)
            {
                reply.Zombies.Add(h.Marker);
                if (notice && GeoMath.Distance(position, h.Marker.Position) <= HordeRules.NoticeMetres)
                    _ = GrainFactory.GetGrain<IHordeGrain>(h.Marker.Id.TrimEnd('#')).Notice(position, nowMs);
            }

        var near = reply.Zombies.Select(z => (marker: z, metres: GeoMath.Distance(position, z.Position))).OrderBy(z => z.metres).ToList();
        if (allowFights && CanStartMapFight(nowMs / 1000) && near.Count > 0 && near[0].metres <= ZombieRules.EngageMetres)
        {
            var kinds = new List<ZombieKind>();
            var lanes = new List<int>();
            var fighters = new List<string>();
            foreach (var (marker, metres) in near)
            {
                if (metres > ZombieRules.JoinMetres || kinds.Count >= ZombieRules.MaxInFight) break;
                int n = Math.Min(marker.Count, ZombieRules.MaxInFight - kinds.Count);
                int lane = LaneTowards(position, marker.Position);
                for (int k = 0; k < n; k++)
                {
                    kinds.Add(marker.Kind);
                    lanes.Add(lane);
                }
                fighters.Add($"{marker.Id}|{n}");
            }
            var buildings = reports[disk.FindIndex(d => d.cell == here)].Buildings;
            reply.Encounter = await StartFight(ZombieRules.ArenaFor(buildings), kinds.Count, kinds, lanes);
            state.State.FightZombies = fighters;
            _ = GrainFactory.GetGrain<IZombieCellGrain>(here).MakeNoise(ZombieRules.FightNoise, position);
        }
        await state.WriteStateAsync();
        return reply;
    }

    /// <summary>The encounter lane (0 = N, clockwise in 45° steps) a zombie at <paramref name="zombie"/> comes from.</summary>
    private static int LaneTowards(LatLng player, LatLng zombie)
    {
        var v = GeoMath.ToLocal(player, zombie);
        double bearing = Math.Atan2(v.X, v.Y) * 180 / Math.PI; // 0 = north, 90 = east
        return Lanes.Rotate((int)Math.Round(bearing / 45.0), 0);
    }

    /// <summary>Asleep at the safehouse, dead or mid-fight: zombies don't come for you (yet).</summary>
    private bool CanBeNoticed()
    {
        if (state.State.ActiveEncounterId != null) return false;
        var body = Body();
        return !body.Dead && !body.Asleep;
    }

    private bool CanStartMapFight(long now) =>
        CanBeNoticed() && now - state.State.MapFightEnded >= ZombieRules.FightCooldownSeconds;

    /// <summary>After a fight that started on the map: the zombies you killed are gone from their groups.</summary>
    private async Task EndMapFight(EncounterView view)
    {
        var fighters = state.State.FightZombies;
        if (fighters == null) return;
        state.State.FightZombies = null;
        state.State.MapFightEnded = Now();

        // The fight doesn't know which of its zombies came from which group: the nearest groups lose them first.
        int total = 0;
        foreach (var f in fighters) total += int.Parse(f[(f.LastIndexOf('|') + 1)..]);
        int killed = Math.Max(0, total - view.ZombiesRemaining);
        var kills = new List<Task<int>>();
        foreach (var f in fighters)
        {
            if (killed <= 0) break;
            int bar = f.LastIndexOf('|');
            string id = f[..bar];
            int n = Math.Min(killed, int.Parse(f[(bar + 1)..]));
            killed -= n;
            if (ZombieRules.TryParseMarker(id, out var cell, out int group))
                kills.Add(GrainFactory.GetGrain<IZombieCellGrain>(cell).Kill(group, n));
            else if (HordeRules.TryParseMarker(id, out var horde))
                kills.Add(GrainFactory.GetGrain<IHordeGrain>(horde).Kill(n));
        }
        await Task.WhenAll(kills);
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

    /// <summary>Doing anything (other than looking) wakes you up. Throws if you're dead.</summary>
    private BodyState RequireAlive()
    {
        var body = Body();
        if (body.Dead) throw new GameRuleException("dead", $"You're dead ({body.DeathCause}). Start a new survivor.");
        BodyRules.Wake(body);
        return body;
    }

    private HealthMessage Health()
    {
        long now = Now();
        return new HealthMessage
        {
            Body = BodyRules.ToView(Body(), now), Inventory = Inv, Now = now,
            Safehouse = state.State.Safehouse, SurvivorNumber = state.State.SurvivorNumber,
        };
    }

    public Task<HealthMessage> GetHealth() => Task.FromResult(Health());

    public async Task<HealthMessage> Treat(int woundId, TreatAction action)
    {
        if (state.State.ActiveEncounterId != null) throw new GameRuleException("in_encounter", "Finish the fight first.");
        var error = BodyRules.Treat(RequireAlive(), Inv, woundId, action, Now());
        if (error != null) throw new GameRuleException("treat", error);
        await state.WriteStateAsync();
        return Health();
    }

    public async Task<HealthMessage> TakeMedicine(string stackId)
    {
        var error = BodyRules.TakeMedicine(RequireAlive(), Inv, stackId, Now());
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

    /// <summary>
    /// The next survivor in your line. What the dead one carried is gone (later: a corpse to loot);
    /// the safehouse and everything in its stash are kept.
    /// </summary>
    public async Task<HealthMessage> NewSurvivor()
    {
        if (!Body().Dead) throw new GameRuleException("not_dead", "You're still alive.");
        state.State.Body = new BodyState { LastUpdated = Now() };
        state.State.Inventory = StarterInventory();
        state.State.ActiveEncounterId = null;
        state.State.EncounterSupplies = null;
        state.State.SurvivorNumber++;
        await state.WriteStateAsync();
        return Health();
    }

    // ------------------------------------------------------------------ food, sleep, safehouse

    public const double StashCapacityKg = 60;

    public async Task<HealthMessage> EatOrDrink(string stackId)
    {
        NotInEncounter("No time to eat.");
        var body = RequireAlive();
        var error = BodyRules.EatOrDrink(body, Inv, stackId, Now());
        if (error != null) throw new GameRuleException("eat", error);
        await state.WriteStateAsync();
        return Health();
    }

    public async Task<HealthMessage> Sleep()
    {
        NotInEncounter("Not now.");
        if (state.State.Safehouse == null) throw new GameRuleException("no_safehouse", "You need a safehouse to sleep in.");
        var body = Body();
        var error = BodyRules.Sleep(body);
        if (error != null) throw new GameRuleException("sleep", error);
        await state.WriteStateAsync();
        return Health();
    }

    public async Task<HealthMessage> Wake()
    {
        BodyRules.Wake(Body());
        await state.WriteStateAsync();
        return Health();
    }

    public Task<string?> SafehouseId() => Task.FromResult(state.State.Safehouse?.BuildingId);

    public async Task<HealthMessage> ClaimSafehouse(string buildingId, BuildingKind kind)
    {
        RequireAlive();
        var current = state.State.Safehouse;
        if (current?.BuildingId == buildingId) throw new GameRuleException("safehouse", "This is already your safehouse.");
        if (current != null && current.Stash.Items.Count > 0)
            throw new GameRuleException("safehouse", "Empty your stash first: it stays in your old safehouse.");
        state.State.Safehouse = new SafehouseView
        {
            BuildingId = buildingId,
            Kind = kind,
            Stash = new ItemContainer { Id = "stash", Name = "Stash", Capacity = StashCapacityKg },
        };
        await state.WriteStateAsync();
        return Health();
    }

    public async Task<HealthMessage> StoreInSafehouse(string stackId, int count)
    {
        RequireAlive();
        var stash = Stash();
        if (!Inv.TryFind(stackId, out var from, out _)) throw Missing();
        Check(InventoryRules.Move(from, stash, stackId, count, Inv.NewId, renameAll: false, out _));
        await state.WriteStateAsync();
        return Health();
    }

    public async Task<HealthMessage> RetrieveFromSafehouse(string stackId, int count, string to)
    {
        RequireAlive();
        var stash = Stash();
        var target = Inv.Container(to) ?? throw new GameRuleException("bad_target", "Put it in your pockets or a bag on your back.");
        if (stash.Find(stackId) == null) throw new GameRuleException("no_such_item", "That isn't in your stash.");
        // New IDs: the stash may hold items from an earlier survivor, whose IDs this inventory could reuse.
        Check(InventoryRules.Move(stash, target, stackId, count, Inv.NewId, renameAll: true, out _));
        await state.WriteStateAsync();
        return Health();
    }

    private ItemContainer Stash() =>
        state.State.Safehouse?.Stash ?? throw new GameRuleException("no_safehouse", "You don't have a safehouse.");

    private void NotInEncounter(string message)
    {
        if (state.State.ActiveEncounterId != null) throw new GameRuleException("in_encounter", message);
    }

    private static GameRuleException Missing() => new("no_such_item", "You don't have that.");

    private static void Check(string? error)
    {
        if (error != null) throw new GameRuleException("inventory", error);
    }
}
