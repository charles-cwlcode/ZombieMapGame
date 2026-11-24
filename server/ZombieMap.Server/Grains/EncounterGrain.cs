using ZombieMap.Core.Encounters;

namespace ZombieMap.Server.Grains;

[GenerateSerializer]
public sealed class EncounterGrainState
{
    [Id(0)] public string? OwnerId { get; set; }
    [Id(1)] public Encounter? Encounter { get; set; }
}

/// <summary>
/// Runs one encounter on the server. The client sends actions and gets back events plus
/// what it's allowed to see; the rolls never leave this grain, so they can't be cheated.
/// </summary>
public sealed class EncounterGrain(
    [PersistentState("encounter")] IPersistentState<EncounterGrainState> state) : Grain, IEncounterGrain
{
    public async Task<EncounterResult> Start(string ownerId, EncounterConfig config)
    {
        if (state.State.Encounter != null)
            throw new GameRuleException("encounter_exists", "This encounter has already started.");

        state.State.OwnerId = ownerId;
        state.State.Encounter = Encounter.Create(config);
        await state.WriteStateAsync();

        return new EncounterResult { Accepted = true, View = state.State.Encounter.BuildView() };
    }

    public async Task<EncounterResult> Act(string ownerId, EncounterAction action)
    {
        var encounter = state.State.Encounter
                        ?? throw new GameRuleException("no_such_encounter", "That encounter doesn't exist.");
        if (state.State.OwnerId != ownerId)
            throw new GameRuleException("not_yours", "That isn't your encounter.");

        var result = encounter.Act(action);
        if (result.Accepted)
        {
            await state.WriteStateAsync();
            // Finished encounters don't need to stay in memory.
            if (encounter.Status != EncounterStatus.Ongoing) DeactivateOnIdle();
        }
        return result;
    }
}
