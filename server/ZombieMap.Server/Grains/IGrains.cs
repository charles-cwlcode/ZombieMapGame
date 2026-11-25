using ZombieMap.Core.Geo;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Health;
using ZombieMap.Core.Items;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Server.Grains;

// Types from ZombieMap.Core (Encounter, FloorPlan, …) can't carry Orleans attributes because
// Unity compiles the same code. They cross grain boundaries through the Newtonsoft JSON codec
// registered in Program.cs. Server-only types use [GenerateSerializer] as normal.

/// <summary>One per player, keyed by player ID. Owns the player's state and their active encounter.</summary>
public interface IPlayerGrain : IGrainWithStringKey
{
    Task<WelcomeMessage> Hello(string? displayName);
    Task<EncounterStateMessage> StartEncounter(StartEncounterRequest request);
    Task<EncounterStateMessage> Act(string encounterId, EncounterAction action);

    // Inventory and looting. The gateway checks loot range before calling these.
    Task<PlayerInventory> GetInventory();
    Task<PlayerInventory> Move(string stackId, int count, string to);
    Task<PlayerInventory> Equip(string stackId);
    Task<PlayerInventory> Unequip();
    Task<PlayerInventory> Drop(string stackId, int count);
    Task<LootMessage> Search(string buildingId, string containerId);
    Task<LootMessage> Take(string buildingId, string containerId, string stackId, int count, string to);
    Task<LootMessage> Put(string buildingId, string containerId, string stackId, int count);

    // Health and clothing.
    Task<HealthMessage> GetHealth();
    Task<HealthMessage> Treat(int woundId, TreatAction action);
    Task<HealthMessage> TakeMedicine(string stackId);
    Task<HealthMessage> Wear(string stackId);
    Task<HealthMessage> TakeOff(string stackId);
    Task<HealthMessage> NewSurvivor();

    /// <summary>The player is here: zombies around them, and a fight if they walked into some.</summary>
    Task<NearbyMessage> Move(LatLng position, bool allowFights);

    // Food, sleep and the safehouse. The gateway checks you're at the safehouse before Sleep/Store/Retrieve.
    Task<HealthMessage> EatOrDrink(string stackId);
    Task<HealthMessage> Sleep();
    Task<HealthMessage> Wake();
    Task<string?> SafehouseId();
    Task<HealthMessage> ClaimSafehouse(string buildingId, BuildingKind kind, LatLng position);
    Task<HealthMessage> StoreInSafehouse(string stackId, int count);
    Task<HealthMessage> RetrieveFromSafehouse(string stackId, int count, string to);
}

/// <summary>One player's loot in one building, keyed "{playerId}|{buildingId}". Only that PlayerGrain calls it.</summary>
public interface ILootGrain : IGrainWithStringKey
{
    Task<LootMessage> Search(string containerId);
    Task<ItemContainer> Get(string containerId);
    Task<ItemStack> Remove(string containerId, string stackId, int count);
    Task Add(string containerId, ItemStack stack);
}

/// <summary>One per encounter. Only its owning PlayerGrain talks to it.</summary>
public interface IEncounterGrain : IGrainWithStringKey
{
    Task<EncounterResult> Start(string ownerId, EncounterConfig config);
    Task<EncounterResult> Act(string ownerId, EncounterAction action);
    Task<Survivor> Outcome(string ownerId);
}

/// <summary>One per real-world building, keyed by its stable building ID.</summary>
public interface IBuildingGrain : IGrainWithStringKey
{
    /// <param name="devFootprint">
    /// Dev only: used when the map data doesn't know this ID (hand-made test buildings).
    /// Real buildings always use the server's own footprint.
    /// </param>
    Task<FloorPlan> GetFloorPlan(BuildingFootprint? devFootprint);
}

/// <summary>Thrown for player mistakes that should go back to the client as an error message.</summary>
[GenerateSerializer]
public sealed class GameRuleException : Exception
{
    [Id(0)] public string Code { get; set; } = "error";

    public GameRuleException() { }

    public GameRuleException(string code, string message) : base(message) => Code = code;
}
