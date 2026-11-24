using ZombieMap.Core.Buildings;
using ZombieMap.Core.Encounters;
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
}

/// <summary>One per encounter. Only its owning PlayerGrain talks to it.</summary>
public interface IEncounterGrain : IGrainWithStringKey
{
    Task<EncounterResult> Start(string ownerId, EncounterConfig config);
    Task<EncounterResult> Act(string ownerId, EncounterAction action);
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
