using Xunit;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Health;
using ZombieMap.Core.Items;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Core.Tests;

public class HealthTests
{
    private const long T0 = 1_800_000_000;

    private static BodyState Body(params Wound[] wounds)
    {
        var b = new BodyState { LastUpdated = T0 };
        foreach (var w in wounds)
        {
            w.Id = ++b.NextWoundId;
            if (w.CreatedAt == 0) w.CreatedAt = T0;
            b.Wounds.Add(w);
        }
        return b;
    }

    /// <summary>Advance in steps short enough to count as online play.</summary>
    private static void PlayFor(BodyState b, long seconds)
    {
        long end = b.LastUpdated + seconds;
        while (b.LastUpdated < end) BodyRules.Advance(b, Math.Min(end, b.LastUpdated + 60));
    }

    private static PlayerInventory Inv(params (string id, int count)[] items)
    {
        var inv = new PlayerInventory();
        foreach (var (id, count) in items)
        {
            var def = ItemCatalog.Get(id);
            inv.Main.Items.Add(new ItemStack { Id = inv.NewId(), ItemId = id, Count = def.Uses > 0 ? 1 : count, UsesLeft = def.Uses > 0 ? count : 0 });
        }
        return inv;
    }

    [Fact]
    public void BleedingDrainsBloodWhilePlaying()
    {
        var b = Body(new Wound { Part = BodyPart.LeftForearm, Kind = WoundKind.Laceration, Bleeding = true });
        PlayFor(b, 200);
        Assert.Equal(90, b.Blood); // 1 per 20 s
    }

    [Fact]
    public void BleedingKeepsGoingWhileYouAreOffline()
    {
        var b = Body(new Wound { Part = BodyPart.LeftForearm, Kind = WoundKind.Laceration, Bleeding = true });
        BodyRules.Advance(b, T0 + 10 * 3600); // came back 10 hours later
        Assert.True(b.Dead);
        Assert.Equal("Bled out", b.DeathCause);
    }

    [Fact]
    public void ALongGapIsWorkedThroughInOrder()
    {
        // A scratch clots after 3 minutes: one big jump costs only those 3 minutes of blood (9), not 200 s worth.
        var b = Body(new Wound { Part = BodyPart.LeftHand, Kind = WoundKind.Scratch, Bleeding = true });
        BodyRules.Advance(b, T0 + 200);
        Assert.Equal(91, b.Blood);
        Assert.False(b.Wounds[0].Bleeding);
    }

    [Fact]
    public void ScratchesStopBleedingOnTheirOwn()
    {
        var b = Body(new Wound { Part = BodyPart.LeftHand, Kind = WoundKind.Scratch, Bleeding = true });
        PlayFor(b, BodyRules.ScratchClotSeconds + 60);
        Assert.False(b.Wounds[0].Bleeding);
    }

    [Fact]
    public void UnbandagedBleedingKills()
    {
        var b = Body(new Wound { Part = BodyPart.Neck, Kind = WoundKind.Bite, Bleeding = true });
        PlayFor(b, 3600);
        Assert.True(b.Dead);
        Assert.Equal("Bled out", b.DeathCause);
    }

    [Fact]
    public void UndisinfectedWoundsGetInfected()
    {
        var b = Body(
            new Wound { Part = BodyPart.LeftHand, Kind = WoundKind.Scratch, Bandage = BandageKind.Rag, BandagedAt = T0 },
            new Wound { Part = BodyPart.RightHand, Kind = WoundKind.Scratch, Bandage = BandageKind.Bandage, BandagedAt = T0, Disinfected = true });
        PlayFor(b, (long)BodyRules.InfectAfterSeconds + 60);
        Assert.True(b.Wounds.Single(w => w.Part == BodyPart.LeftHand).Infected);
        Assert.False(b.Wounds.Single(w => w.Part == BodyPart.RightHand).Infected);
    }

    [Fact]
    public void DirtyBandagesCauseInfectionEvenWhenDisinfected()
    {
        var w = new Wound { Part = BodyPart.LeftForearm, Kind = WoundKind.Bite, Bandage = BandageKind.Rag, BandagedAt = T0, Disinfected = true };
        var b = Body(w);
        BodyRules.Advance(b, T0 + BodyRules.RagDirtySeconds + 1); // the rag gets dirty, no infection yet
        Assert.True(BodyRules.BandageDirty(w, b.LastUpdated));
        Assert.False(w.Infected);
        BodyRules.Advance(b, b.LastUpdated + (long)BodyRules.InfectAfterSeconds + 60); // offline counts too
        Assert.True(w.Infected);
    }

    [Fact]
    public void TreatedWoundsHealEvenOffline()
    {
        var b = Body(new Wound { Part = BodyPart.LeftHand, Kind = WoundKind.Scratch, Bandage = BandageKind.Bandage, BandagedAt = T0, Disinfected = true });
        BodyRules.Advance(b, T0 + 3600);
        Assert.Equal(0.5, b.Wounds[0].Healed, 2);
        BodyRules.Advance(b, T0 + 2 * 3600 + 1);
        Assert.Empty(b.Wounds);
    }

    [Fact]
    public void InfectedWoundsDoNotHeal()
    {
        var b = Body(new Wound { Part = BodyPart.LeftHand, Kind = WoundKind.Scratch, Bandage = BandageKind.Bandage, BandagedAt = T0, Infected = true });
        BodyRules.Advance(b, T0 + 5 * 3600);
        Assert.Single(b.Wounds);
        Assert.Equal(0, b.Wounds[0].Healed);
    }

    [Fact]
    public void BloodAndEnduranceComeBack()
    {
        var b = Body();
        b.Blood = 50;
        b.Endurance = 10;
        BodyRules.Advance(b, T0 + 3600);
        Assert.Equal(70, b.Blood);
        Assert.Equal(100, b.Endurance);
    }

    [Fact]
    public void TreatingUsesItemsFromTheInventory()
    {
        var b = Body(new Wound { Part = BodyPart.LeftForearm, Kind = WoundKind.Laceration, Bleeding = true });
        var inv = Inv(("med.rag", 2), ("med.alcohol_wipes", 1), ("med.suture_needle", 1));
        int id = b.Wounds[0].Id;

        Assert.Null(BodyRules.Treat(b, inv, id, TreatAction.BandageWithRag, T0));
        Assert.False(b.Wounds[0].Bleeding);
        Assert.Equal(WoundTreatment.Patched, b.Wounds[0].Treatment);
        Assert.Equal(1, inv.CountOf(d => d.Medical == MedicalUse.Rag));

        Assert.Null(BodyRules.Treat(b, inv, id, TreatAction.Disinfect, T0));
        Assert.Equal(WoundTreatment.Treated, b.Wounds[0].Treatment);
        Assert.NotNull(BodyRules.Treat(b, inv, id, TreatAction.Disinfect, T0)); // wipes used up
        Assert.NotNull(BodyRules.Treat(b, inv, id, TreatAction.BandageWithBandage, T0)); // no bandages

        Assert.Null(BodyRules.Treat(b, inv, id, TreatAction.Stitch, T0));
        Assert.True(b.Wounds[0].Stitched);
        Assert.DoesNotContain(inv.Main.Items, s => s.ItemId == "med.suture_needle");
    }

    [Fact]
    public void OnlyLacerationsCanBeStitched()
    {
        var b = Body(new Wound { Part = BodyPart.Neck, Kind = WoundKind.Bite });
        Assert.NotNull(BodyRules.Treat(b, Inv(("med.suture_needle", 1)), b.Wounds[0].Id, TreatAction.Stitch, T0));
    }

    [Fact]
    public void DisinfectantBottleUsesGoDownOneAtATime()
    {
        var b = Body(new Wound { Part = BodyPart.LeftHand, Kind = WoundKind.Scratch });
        var inv = Inv(("med.disinfectant", 3));
        Assert.Null(BodyRules.Treat(b, inv, b.Wounds[0].Id, TreatAction.Disinfect, T0));
        Assert.Equal(2, inv.Main.Items.Single().UsesLeft);
    }

    [Fact]
    public void AntibioticsCureAndPreventInfection()
    {
        var b = Body(new Wound { Part = BodyPart.LeftHand, Kind = WoundKind.Scratch, Infected = true });
        var inv = Inv(("med.antibiotics", 2));
        Assert.Null(BodyRules.TakeMedicine(b, inv, inv.Main.Items[0].Id, T0));
        Assert.False(b.Wounds[0].Infected);
        PlayFor(b, (long)BodyRules.InfectAfterSeconds * 2);
        Assert.False(b.Wounds[0].Infected);
        Assert.Equal(1, inv.Main.Items[0].UsesLeft);
    }

    [Fact]
    public void EncounterSuppliesComeFromTheInventoryAndGetUsedUp()
    {
        var inv = Inv(("med.rag", 3), ("med.bandage", 2), ("med.disinfectant", 4), ("med.alcohol_wipes", 2));
        var before = BodyRules.SuppliesFrom(inv);
        Assert.Equal((3, 2, 6), (before.Rags, before.Bandages, before.Disinfectant));

        var after = before.Clone();
        after.Rags -= 2;
        after.Disinfectant -= 3;
        BodyRules.ConsumeUsed(inv, before, after);
        var now = BodyRules.SuppliesFrom(inv);
        Assert.Equal((1, 2, 3), (now.Rags, now.Bandages, now.Disinfectant));
    }

    [Fact]
    public void EncounterWoundsComeBackWithIdsAndKeepOldOnes()
    {
        var b = Body(new Wound { Part = BodyPart.LeftHand, Kind = WoundKind.Scratch });
        var e = Encounter.Create(new EncounterConfig { Seed = 5, StartWounds = b.Wounds, StartBlood = 70 });
        Assert.Equal(70, e.Survivor.Blood);
        e.Survivor.Wounds.Add(new Wound { Part = BodyPart.Neck, Kind = WoundKind.Bite, Bleeding = true, CarriesVirus = true });

        BodyRules.ApplyEncounter(b, e.Survivor, died: false, deathCause: null, now: T0 + 50);
        Assert.Equal(new[] { 1, 2 }, b.Wounds.Select(w => w.Id).ToArray());
        Assert.Equal(T0 + 50, b.Wounds[1].CreatedAt);
        Assert.True(b.Wounds[1].CarriesVirus); // the server keeps the truth…
        Assert.DoesNotContain("CarriesVirus", ProtocolJson.Serialize(BodyRules.ToView(b, T0))); // …the client never sees it
    }

    [Fact]
    public void WearingClothesSwapsWhatsInTheSlot()
    {
        var inv = Inv(("clothing.hoodie", 1), ("clothing.leather_jacket", 1), ("med.rag", 1));
        var hoodie = inv.Main.Items[0].Id;
        var jacket = inv.Main.Items[1].Id;
        Assert.Null(InventoryRules.Wear(inv, hoodie));
        Assert.Null(InventoryRules.Wear(inv, jacket));
        Assert.Equal(jacket, inv.Worn.Single().Id);
        Assert.NotNull(inv.Main.Find(hoodie));
        Assert.NotNull(InventoryRules.Wear(inv, inv.Main.Items.First(s => s.ItemId == "med.rag").Id));
        Assert.Null(InventoryRules.TakeOff(inv, jacket));
        Assert.Empty(inv.Worn);
    }

    [Fact]
    public void ClothesStopSomeHitsAndWearDown()
    {
        (int wounds, int blocks, double jacket) Run(bool dressed)
        {
            int wounds = 0, blocks = 0;
            double jacketCondition = 0;
            for (ulong seed = 1; seed <= 150; seed++)
            {
                var worn = dressed
                    ? new[] { "clothing.leather_jacket", "clothing.gloves", "clothing.scarf", "clothing.bike_helmet" }
                        .Select((id, i) => new ItemStack { Id = "w" + i, ItemId = id }).ToList()
                    : null;
                var e = Encounter.Create(new EncounterConfig
                {
                    Seed = seed, Worn = worn, StartBlood = 1000,
                    Spawns = new List<ZombieSpawn> { new() { Kind = ZombieKind.Shambler, Lane = 0, Ring = 0 } },
                });
                for (int i = 0; i < 6 && e.Status == EncounterStatus.Ongoing; i++)
                {
                    var r = e.Act(EncounterAction.Of(EncounterActionKind.Wait));
                    wounds += r.Events.Count(ev => ev.Kind == EncounterEventKind.Wounded);
                    blocks += r.Events.Count(ev => ev.Kind == EncounterEventKind.Blocked);
                }
                if (dressed) jacketCondition += e.Survivor.Worn[0].Condition;
            }
            return (wounds, blocks, jacketCondition / 150);
        }

        var naked = Run(false);
        var dressed = Run(true);
        Assert.Equal(0, naked.blocks);
        Assert.True(dressed.blocks > 50, $"only {dressed.blocks} blocks");
        Assert.True(dressed.wounds < naked.wounds * 0.7, $"dressed {dressed.wounds} vs naked {naked.wounds} wounds");
        Assert.True(dressed.jacket < 1, "the jacket should wear down");
    }

    [Fact]
    public void NoClothesMeansTheSameFightAsBefore()
    {
        // Encounters without clothes must not consume extra dice rolls (keeps old fights reproducible).
        string Play(List<ItemStack> worn)
        {
            var e = Encounter.Create(new EncounterConfig { Seed = 42, Worn = worn });
            var log = new List<string>();
            for (int i = 0; i < 20 && e.Status == EncounterStatus.Ongoing; i++)
                log.AddRange(e.Act(EncounterAction.Of(EncounterActionKind.Wait)).Events.Select(ev => ev.Kind.ToString()));
            return string.Join(",", log);
        }
        Assert.Equal(Play(null), Play(new List<ItemStack>()));
    }

    // ---------- hunger, thirst, sleep ----------

    [Fact]
    public void ThirstEmptiesInADayThenKillsAboutTwoDaysLater()
    {
        var b = Body();
        BodyRules.Advance(b, T0 + 24 * 3600);
        Assert.True(b.Water <= 0.01);
        Assert.False(b.Dead);
        Assert.True(b.Food > 60);
        BodyRules.Advance(b, T0 + 70 * 3600);
        Assert.True(b.Blood < 30 && !b.Dead, $"blood {b.Blood}");
        BodyRules.Advance(b, T0 + 74 * 3600);
        Assert.True(b.Dead);
        Assert.Equal("Dehydration", b.DeathCause);
    }

    [Fact]
    public void StarvingIsSlowerThanThirst()
    {
        var b = Body();
        b.Water = 1e9; // never thirsty, to see hunger on its own
        BodyRules.Advance(b, T0 + 72 * 3600 + 60);
        Assert.Equal(0, b.Food);
        Assert.False(b.Dead);
        BodyRules.Advance(b, T0 + (72 + 9 * 24) * 3600);
        Assert.False(b.Dead);
        BodyRules.Advance(b, T0 + (72 + 11 * 24) * 3600);
        Assert.True(b.Dead);
        Assert.Equal("Starvation", b.DeathCause);
    }

    [Fact]
    public void EatingAndDrinkingFillYouUp()
    {
        var b = Body();
        b.Food = 20;
        b.Water = 20;
        var inv = Inv(("food.canned_beans", 2), ("drink.water_bottle", 1));
        Assert.Null(BodyRules.EatOrDrink(b, inv, inv.Main.Items[0].Id, T0));
        Assert.Equal(20 + 380 / 60.0, b.Food, 3);
        Assert.Equal(1, inv.Main.Items[0].Count);
        var water = inv.Main.Items.Single(s => s.ItemId == "drink.water_bottle");
        Assert.Null(BodyRules.EatOrDrink(b, inv, water.Id, T0));
        Assert.Equal(20 + 500 / 20.0, b.Water, 3);
        Assert.DoesNotContain(inv.Main.Items, s => s.ItemId == "drink.water_bottle");
    }

    [Fact]
    public void SomeThingsCantBeEaten()
    {
        var b = Body();
        b.Food = 10;
        var inv = Inv(("food.rice", 1), ("food.bread", 1), ("med.rag", 1));
        Assert.Contains("cooking", BodyRules.EatOrDrink(b, inv, inv.Main.Items[0].Id, T0));
        inv.Main.Items[1].FreshUntilDay = T0 / 86400 - 1;
        Assert.Contains("gone off", BodyRules.EatOrDrink(b, inv, inv.Main.Items[1].Id, T0));
        Assert.Contains("can't eat", BodyRules.EatOrDrink(b, inv, inv.Main.Items[2].Id, T0));
        b.Food = 100;
        inv.Main.Items[1].FreshUntilDay = 0;
        Assert.Equal("You're not hungry.", BodyRules.EatOrDrink(b, inv, inv.Main.Items[1].Id, T0));
        Assert.Equal(3, inv.Main.Items.Count);
    }

    [Fact]
    public void SleepNeedsTirednessAndNoBleeding()
    {
        var b = Body();
        Assert.Equal("You're not tired.", BodyRules.Sleep(b));
        b.Fatigue = 60;
        b.Wounds.Add(new Wound { Id = 1, Part = BodyPart.LeftHand, Kind = WoundKind.Laceration, Bleeding = true, CreatedAt = T0 });
        Assert.Contains("bleed", BodyRules.Sleep(b));
        b.Wounds.Clear();
        Assert.Null(BodyRules.Sleep(b));
        Assert.True(b.Asleep);
    }

    [Fact]
    public void SleepingRestsYouAndSlowsHungerAndThirst()
    {
        var awake = Body();
        var asleep = Body();
        awake.Fatigue = asleep.Fatigue = 80;
        asleep.Asleep = true;
        BodyRules.Advance(awake, T0 + 8 * 3600);
        BodyRules.Advance(asleep, T0 + 8 * 3600);
        Assert.Equal(0, asleep.Fatigue, 3);
        Assert.Equal(100, awake.Fatigue, 3);
        Assert.True(asleep.Water > awake.Water + 10);
    }

    [Fact]
    public void ExhaustionCapsEndurance()
    {
        var b = Body();
        b.Fatigue = 95;
        b.Endurance = 100;
        BodyRules.Advance(b, T0 + 60);
        Assert.Equal(50, b.Endurance);
        Assert.Equal(50, BodyRules.ToView(b, T0 + 60).EnduranceCap);
    }
}
