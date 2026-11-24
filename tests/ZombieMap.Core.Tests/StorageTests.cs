using Newtonsoft.Json;
using Xunit;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Health;
using ZombieMap.Core.Items;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Core.Tests;

/// <summary>
/// The server saves grain state as JSON with <see cref="ProtocolJson.StorageSettings"/>. Whatever is
/// saved must come back exactly, or a restart would change the game.
/// </summary>
public class StorageTests
{
    private static T RoundTrip<T>(T value) =>
        JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value, ProtocolJson.StorageSettings), ProtocolJson.StorageSettings)!;

    private static string Wire(object o) => ProtocolJson.Write("x", o);

    [Fact]
    public void AnEncounterCarriesOnExactlyAfterBeingSaved()
    {
        var live = Encounter.Create(new EncounterConfig { Seed = 12345, ZombieCount = 5 });
        for (int i = 0; i < 3; i++) live.Act(EncounterAction.Of(EncounterActionKind.Wait));

        var saved = RoundTrip(live);
        Assert.Equal(live.Rng.State, saved.Rng.State);
        for (int i = 0; i < 15 && live.Status == EncounterStatus.Ongoing; i++)
        {
            var action = i % 3 == 0 ? EncounterAction.Turn(1) : EncounterAction.Of(EncounterActionKind.Wait);
            Assert.Equal(Wire(live.Act(action)), Wire(saved.Act(action)));
        }
        Assert.Equal(Wire(live.BuildView()), Wire(saved.BuildView()));
    }

    [Fact]
    public void BodyAndInventoryComeBackTheSame()
    {
        var body = new BodyState { Blood = 71, Food = 42.5, Water = 13.25, Fatigue = 60, Asleep = true, LastUpdated = 1_800_000_000, AntibioticsUntil = 1_800_003_600 };
        body.Wounds.Add(new Wound { Id = 3, Part = BodyPart.LeftForearm, Kind = WoundKind.Laceration, Bandage = BandageKind.Rag, BandagedAt = 1_799_999_000, Healed = 0.25, CarriesVirus = true });
        var copy = RoundTrip(body);
        Assert.Equal(JsonConvert.SerializeObject(body, ProtocolJson.StorageSettings), JsonConvert.SerializeObject(copy, ProtocolJson.StorageSettings));
        Assert.True(copy.Wounds[0].CarriesVirus); // the hidden virus roll must survive a restart

        var inv = new PlayerInventory();
        var bag = new ItemStack { Id = inv.NewId(), ItemId = "bag.hiking" };
        InventoryRules.InitBag(bag);
        bag.Contents.Items.Add(new ItemStack { Id = inv.NewId(), ItemId = "food.canned_beans", Count = 3 });
        inv.Back = bag;
        inv.Main.Items.Add(new ItemStack { Id = inv.NewId(), ItemId = "med.rag", Count = 2 });
        inv.Worn.Add(new ItemStack { Id = inv.NewId(), ItemId = "clothing.jeans", Condition = 0.8 });
        var invCopy = RoundTrip(inv);
        Assert.Equal(Wire(inv), Wire(invCopy));
        Assert.Equal(inv.NextId, invCopy.NextId); // new items must not reuse IDs
        Assert.Single(invCopy.Main.Items);        // defaults aren't doubled up
    }

    [Fact]
    public void DictionaryKeysKeepTheirCase()
    {
        var saved = RoundTrip(new Dictionary<string, int> { ["Kitchen.0"] = 1, ["r2.Fridge"] = 2 });
        Assert.Equal(new[] { "Kitchen.0", "r2.Fridge" }, saved.Keys.OrderBy(k => k).ToArray());
    }
}
