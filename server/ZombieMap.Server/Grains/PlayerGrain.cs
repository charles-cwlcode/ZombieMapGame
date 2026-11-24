using ZombieMap.Core.Encounters;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Server.Grains;

[GenerateSerializer]
public sealed class PlayerState
{
    [Id(0)] public string DisplayName { get; set; } = "Survivor";
    [Id(1)] public DateTime CreatedUtc { get; set; }
    [Id(2)] public string? ActiveEncounterId { get; set; }
    [Id(3)] public int EncountersStarted { get; set; }
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
}
