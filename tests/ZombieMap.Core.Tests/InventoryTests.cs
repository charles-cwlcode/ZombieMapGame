using Xunit;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Items;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Core.Tests;

public class InventoryTests
{
    private static ItemStack Item(string itemId, string id, int count = 1)
    {
        var s = new ItemStack { Id = id, ItemId = itemId, Count = count };
        InventoryRules.InitBag(s);
        return s;
    }

    private static ItemContainer Box(params ItemStack[] items) =>
        new() { Id = "box", Name = "Box", Capacity = 100, Items = items.ToList() };

    [Fact]
    public void CatalogIsSane()
    {
        Assert.True(ItemCatalog.All.Count >= 40);
        foreach (var d in ItemCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Name), d.Id);
            Assert.True(d.Weight > 0, d.Id);
            Assert.True(d.MaxStack >= 1, d.Id);
            Assert.Equal(d.Category == ItemCategory.Bag, d.IsBag);
            Assert.InRange(d.WeightReduction, 0, 0.9);
            Assert.Contains('.', d.Id);
        }
    }

    [Fact]
    public void EveryLootTableItemExists()
    {
        foreach (BuildingKind b in Enum.GetValues(typeof(BuildingKind)))
        foreach (RoomType r in Enum.GetValues(typeof(RoomType)))
        foreach (ContainerKind c in Enum.GetValues(typeof(ContainerKind)))
        {
            var table = LootTables.For(b, r, c);
            Assert.NotEmpty(table.Entries);
            Assert.True(table.MinRolls <= table.MaxRolls);
            foreach (var e in table.Entries) Assert.True(ItemCatalog.TryGet(e.ItemId, out _), $"{b}/{r}/{c}: {e.ItemId}");
        }
    }

    [Fact]
    public void TakingFromABoxRenamesIntoThePlayersIds()
    {
        var inv = new PlayerInventory();
        var box = Box(Item("food.canned_beans", "1.0#0"));
        Assert.Null(InventoryRules.Move(box, inv.Main, "1.0#0", 0, inv.NewId, renameAll: true, out var moved));
        Assert.Empty(box.Items);
        Assert.Equal("i1", moved.Id);
        Assert.Same(moved, inv.Main.Find("i1"));
    }

    [Fact]
    public void PocketsHaveLimitedRoom()
    {
        var inv = new PlayerInventory();
        var box = Box(Item("weapon.crowbar", "a"), Item("weapon.crowbar", "b"), Item("weapon.crowbar", "c"),
                      Item("weapon.crowbar", "d"), Item("weapon.crowbar", "e"));
        for (int i = 0; i < 4; i++) Assert.Null(InventoryRules.Move(box, inv.Main, box.Items[0].Id, 0, inv.NewId, true, out _));
        // 4 crowbars = 8 kg = full pockets.
        string error = InventoryRules.Move(box, inv.Main, "e", 0, inv.NewId, true, out _);
        Assert.NotNull(error);
        Assert.Single(box.Items);
        Assert.Equal(8, inv.CarriedWeight, 3);
    }

    [Fact]
    public void StacksSplitAndMerge()
    {
        var inv = new PlayerInventory();
        var box = Box(Item("med.bandage", "b1", 6), Item("med.bandage", "b2", 7));
        Assert.Null(InventoryRules.Move(box, inv.Main, "b1", 4, inv.NewId, true, out _));
        Assert.Equal(2, box.Find("b1").Count);
        Assert.Null(InventoryRules.Move(box, inv.Main, "b2", 0, inv.NewId, true, out _));
        // 4 + 7 = 11 bandages, max stack 10 → 10 + 1.
        var bandages = inv.Main.Items.Where(s => s.ItemId == "med.bandage").Select(s => s.Count).OrderBy(c => c).ToList();
        Assert.Equal(new[] { 1, 10 }, bandages);
        Assert.Equal(11, inv.CountOf(d => d.Medical == MedicalUse.Bandage));
    }

    [Fact]
    public void WornBagReducesWeightAndAddsRoom()
    {
        var inv = new PlayerInventory();
        var box = Box(Item("bag.hiking", "bag"), Item("food.rice", "r1"), Item("food.rice", "r2"), Item("food.rice", "r3"));
        Assert.Null(InventoryRules.Move(box, inv.Main, "bag", 0, inv.NewId, true, out var bag));
        Assert.Null(InventoryRules.Equip(inv, bag.Id));
        Assert.NotNull(inv.Back);
        Assert.Empty(inv.Main.Items);

        var back = inv.Container(PlayerInventory.BackId);
        foreach (var id in new[] { "r1", "r2", "r3" })
            Assert.Null(InventoryRules.Move(box, back, id, 0, inv.NewId, true, out _));
        // Hiking bag 1.2 kg + 3 kg of rice at 60% off = 1.2 + 1.2.
        Assert.Equal(2.4, inv.CarriedWeight, 3);

        // Carried in the hands it counts in full.
        Assert.Null(InventoryRules.Unequip(inv));
        Assert.Equal(4.2, inv.CarriedWeight, 3);
    }

    [Fact]
    public void CannotWearAPlasticBag()
    {
        var inv = new PlayerInventory();
        inv.Main.Items.Add(Item("bag.plastic", "p"));
        Assert.NotNull(InventoryRules.Equip(inv, "p"));
        Assert.Null(inv.Back);
    }

    [Fact]
    public void FullBagCannotGoInsideAnotherBag()
    {
        var inv = new PlayerInventory();
        inv.Main.Items.Add(Item("bag.hiking", "h"));
        Assert.Null(InventoryRules.Equip(inv, "h"));
        var tote = Item("bag.tote", "t");
        tote.Contents.Items.Add(Item("food.apple", "x"));
        inv.Main.Items.Add(tote);
        Assert.NotNull(InventoryRules.Move(inv.Main, inv.Container(PlayerInventory.BackId), "t", 0, inv.NewId, false, out _));
    }

    [Fact]
    public void SwappingBagsPutsTheOldOneInYourHands()
    {
        var inv = new PlayerInventory();
        inv.Main.Items.Add(Item("bag.school", "s"));
        inv.Main.Items.Add(Item("bag.hiking", "h"));
        Assert.Null(InventoryRules.Equip(inv, "s"));
        Assert.Null(InventoryRules.Equip(inv, "h"));
        Assert.Equal("h", inv.Back.Id);
        Assert.NotNull(inv.Main.Find("s"));
        Assert.Equal("s", inv.Main.Find("s").Contents.Id);
    }

    [Fact]
    public void LootIsDeterministicPerPlayerAndDifferentBetweenPlayers()
    {
        var container = new Container { Id = "0.0", Kind = ContainerKind.Cupboard };
        string Gen(string seed) => ProtocolJson.Serialize(LootGenerator.Generate(seed, BuildingKind.House, RoomType.Kitchen, container, 20000));
        Assert.Equal(Gen("alice|b1|0.0|0"), Gen("alice|b1|0.0|0"));

        int differences = 0;
        for (int i = 0; i < 20; i++)
            if (Gen($"alice|b{i}|0.0|0") != Gen($"bob|b{i}|0.0|0")) differences++;
        Assert.True(differences >= 15, $"only {differences}/20 kitchens differed between players");
    }

    [Fact]
    public void GeneratedLootRespectsCapacityAndHasValidState()
    {
        foreach (ContainerKind kind in Enum.GetValues(typeof(ContainerKind)))
        for (int i = 0; i < 50; i++)
        {
            var c = LootGenerator.Generate($"p|b|{i}", BuildingKind.Pharmacy, RoomType.Dispensary, new Container { Id = "1.2", Kind = kind }, 20000);
            Assert.True(InventoryRules.Used(c) <= c.Capacity + 1e-9);
            Assert.Equal(c.Items.Count, c.Items.Select(s => s.Id).Distinct().Count());
            foreach (var s in c.Items)
            {
                var d = s.Def();
                Assert.InRange(s.Count, 1, d.MaxStack);
                if (d.Uses > 0) Assert.InRange(s.UsesLeft, 1, d.Uses);
                if (d.Spoils) Assert.InRange(s.FreshUntilDay, 20001, 20000 + d.FreshDays);
                if (d.IsBag) Assert.NotNull(s.Contents);
            }
        }
    }

    [Fact]
    public void PharmaciesHaveMoreMedicineThanHouses()
    {
        int Meds(BuildingKind b, RoomType r, ContainerKind k)
        {
            int n = 0;
            for (int i = 0; i < 200; i++)
                n += LootGenerator.Generate($"p|{i}", b, r, new Container { Id = "x", Kind = k }, 20000)
                    .Items.Where(s => s.Def().Category == ItemCategory.Medical).Sum(s => s.Count);
            return n;
        }
        Assert.True(Meds(BuildingKind.Pharmacy, RoomType.Dispensary, ContainerKind.Shelf) >
                    2 * Meds(BuildingKind.House, RoomType.Bathroom, ContainerKind.Cabinet));
    }

    [Fact]
    public void InventoryRoundTripsThroughJson()
    {
        var inv = new PlayerInventory();
        inv.Main.Items.Add(Item("bag.school", "s"));
        Assert.Null(InventoryRules.Equip(inv, "s"));
        inv.Back.Contents.Items.Add(new ItemStack { Id = "i9", ItemId = "food.bread", FreshUntilDay = 20003 });
        inv.Main.Items.Add(new ItemStack { Id = "i8", ItemId = "med.rag", Count = 3 });
        inv.NextId = 9;

        var back = ProtocolJson.Read(ProtocolJson.Write(MessageTypes.Inventory, new InventoryMessage { Inventory = inv }, 1))
            .DataAs<InventoryMessage>().Inventory;
        Assert.Equal(ProtocolJson.Serialize(inv), ProtocolJson.Serialize(back));
        Assert.Equal(inv.CarriedWeight, back.CarriedWeight, 9);
        Assert.Equal("i10", back.NewId());
    }
}
