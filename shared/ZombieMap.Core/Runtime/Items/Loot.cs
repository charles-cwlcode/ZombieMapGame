using System;
using System.Collections.Generic;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Rng;

namespace ZombieMap.Core.Items
{
    public sealed class LootEntry
    {
        public string ItemId;
        /// <summary>Relative chance against the other entries in the table.</summary>
        public double Weight;
        public int MinCount = 1;
        public int MaxCount = 1;

        public LootEntry(string itemId, double weight, int minCount = 1, int maxCount = 1)
        {
            ItemId = itemId;
            Weight = weight;
            MinCount = minCount;
            MaxCount = maxCount;
        }
    }

    public sealed class LootTable
    {
        public int MinRolls;
        public int MaxRolls;
        /// <summary>Chance the container has nothing at all (someone got here first).</summary>
        public double EmptyChance;
        public List<LootEntry> Entries = new List<LootEntry>();
    }

    /// <summary>Per-container search time and capacity, and which loot table a container uses.</summary>
    public static class LootRules
    {
        /// <summary>Real seconds to search one container. Thorough searches (later) take longer and find more.</summary>
        public static int SearchSeconds(ContainerKind kind)
        {
            switch (kind)
            {
                case ContainerKind.Fridge:
                case ContainerKind.Counter:
                case ContainerKind.MedicineCabinet:
                case ContainerKind.Bin:
                    return 2;
                case ContainerKind.Shelf:
                case ContainerKind.Desk:
                case ContainerKind.Cupboard:
                case ContainerKind.Cabinet:
                case ContainerKind.Drawers:
                    return 3;
                case ContainerKind.Wardrobe:
                case ContainerKind.Closet:
                case ContainerKind.Locker:
                case ContainerKind.FilingCabinet:
                    return 4;
                case ContainerKind.Crate:
                    return 5;
                default:
                    return 3;
            }
        }

        /// <summary>How much you can put back into a piece of furniture.</summary>
        public static double Capacity(ContainerKind kind)
        {
            switch (kind)
            {
                case ContainerKind.MedicineCabinet: return 3;
                case ContainerKind.Desk:
                case ContainerKind.Bin: return 10;
                case ContainerKind.Counter:
                case ContainerKind.Drawers:
                case ContainerKind.Locker: return 15;
                case ContainerKind.Fridge:
                case ContainerKind.Cabinet:
                case ContainerKind.FilingCabinet: return 20;
                case ContainerKind.Cupboard:
                case ContainerKind.Shelf: return 25;
                case ContainerKind.Wardrobe:
                case ContainerKind.Closet: return 40;
                case ContainerKind.Crate: return 50;
                default: return 20;
            }
        }

        public static string ContainerName(ContainerKind kind)
        {
            switch (kind)
            {
                case ContainerKind.MedicineCabinet: return "Medicine cabinet";
                case ContainerKind.FilingCabinet: return "Filing cabinet";
                default: return kind.ToString();
            }
        }
    }

    /// <summary>
    /// Which items turn up where. Most specific first: building + room + container, then room +
    /// container, then container alone.
    /// </summary>
    public static class LootTables
    {
        public static LootTable For(BuildingKind building, RoomType room, ContainerKind container)
        {
            // ---- shops and pharmacies ----
            if (building == BuildingKind.Pharmacy &&
                (room == RoomType.Dispensary || container == ContainerKind.MedicineCabinet))
                return Pharmacy(rich: true);
            if (building == BuildingKind.Pharmacy && room == RoomType.SalesFloor)
                return Pharmacy(rich: false);
            if (building == BuildingKind.Shop && room == RoomType.SalesFloor)
                return Groceries;
            if (room == RoomType.Storage && (building == BuildingKind.Shop || building == BuildingKind.Pharmacy))
                return building == BuildingKind.Shop ? ShopStockroom : Pharmacy(rich: false);

            // ---- rooms that change what a container holds ----
            if (container == ContainerKind.Cupboard && room == RoomType.Kitchen) return KitchenCupboard;
            if (container == ContainerKind.Cabinet && (room == RoomType.Bathroom || room == RoomType.Restroom)) return BathroomCabinet;
            if (container == ContainerKind.Shelf && room == RoomType.LivingRoom) return LivingRoomShelf;

            switch (container)
            {
                case ContainerKind.Fridge: return Fridge;
                case ContainerKind.Cupboard: return KitchenCupboard;
                case ContainerKind.Counter: return Counter;
                case ContainerKind.Cabinet: return BathroomCabinet;
                case ContainerKind.MedicineCabinet: return BathroomCabinet;
                case ContainerKind.Wardrobe: return Wardrobe;
                case ContainerKind.Drawers: return Drawers;
                case ContainerKind.Shelf: return LivingRoomShelf;
                case ContainerKind.Closet: return Closet;
                case ContainerKind.Crate: return ShopStockroom;
                case ContainerKind.Desk: return Desk;
                case ContainerKind.FilingCabinet: return FilingCabinet;
                case ContainerKind.Locker: return Locker;
                case ContainerKind.Bin: return Bin;
                default: return Bin;
            }
        }

        private static LootTable Table(int min, int max, double empty, params LootEntry[] entries) =>
            new LootTable { MinRolls = min, MaxRolls = max, EmptyChance = empty, Entries = new List<LootEntry>(entries) };

        private static LootEntry E(string id, double weight, int min = 1, int max = 1) => new LootEntry(id, weight, min, max);

        private static readonly LootTable Fridge = Table(0, 4, 0.2,
            E("food.ready_meal", 3), E("food.cheese", 3), E("food.bread", 2), E("drink.juice", 3), E("drink.water_bottle", 3),
            E("drink.cola", 3), E("food.apple", 3), E("drink.energy", 1));

        private static readonly LootTable KitchenCupboard = Table(1, 4, 0.15,
            E("food.canned_beans", 4), E("food.canned_soup", 4), E("food.canned_tuna", 3), E("food.canned_peaches", 2),
            E("food.pasta", 3), E("food.rice", 2), E("food.cereal", 2), E("food.biscuits", 3), E("food.peanut_butter", 1),
            E("tool.can_opener", 2), E("weapon.kitchen_knife", 1), E("weapon.frying_pan", 1), E("tool.lighter", 1),
            E("bag.plastic", 2), E("med.rag", 1, 1, 3));

        private static readonly LootTable Counter = Table(0, 2, 0.35,
            E("weapon.kitchen_knife", 3), E("food.bread", 2), E("food.apple", 2), E("bag.plastic", 3), E("tool.lighter", 1),
            E("tool.scissors", 1), E("drink.cola", 2), E("food.crisps", 2));

        private static readonly LootTable BathroomCabinet = Table(0, 3, 0.25,
            E("med.bandage", 3, 1, 4), E("med.disinfectant", 2), E("med.painkillers", 3), E("med.rag", 2, 1, 3),
            E("med.alcohol_wipes", 2, 2, 8), E("med.suture_needle", 1, 1, 2), E("tool.scissors", 1), E("med.antibiotics", 0.3));

        private static readonly LootTable Wardrobe = Table(1, 3, 0.15,
            E("clothing.tshirt", 4), E("clothing.hoodie", 3), E("clothing.jeans", 3), E("clothing.leather_jacket", 1),
            E("bag.school", 2), E("bag.duffel", 1), E("bag.tote", 2), E("bag.hiking", 0.4), E("mat.sheet", 2), E("clothing.scarf", 1.5), E("clothing.gloves", 1));

        private static readonly LootTable Drawers = Table(0, 3, 0.25,
            E("clothing.tshirt", 3), E("clothing.gloves", 1), E("mat.sheet", 2), E("tool.batteries", 2, 1, 4), E("tool.torch", 1),
            E("med.painkillers", 1), E("med.rag", 2, 1, 2), E("tool.scissors", 1), E("tool.lighter", 1));

        private static readonly LootTable LivingRoomShelf = Table(0, 3, 0.3,
            E("tool.torch", 2), E("tool.batteries", 2, 2, 6), E("tool.duct_tape", 1), E("food.chocolate", 2), E("food.crisps", 2),
            E("drink.cola", 1), E("tool.lighter", 1), E("weapon.golf_club", 0.3));

        private static readonly LootTable Closet = Table(0, 3, 0.25,
            E("weapon.baseball_bat", 2), E("weapon.golf_club", 1), E("weapon.hammer", 1), E("bag.hiking", 0.5), E("bag.duffel", 1),
            E("tool.torch", 2), E("mat.rope", 1), E("clothing.work_boots", 1), E("clothing.hoodie", 2), E("clothing.bike_helmet", 1),
            E("mat.nails", 1, 10, 40), E("tool.screwdriver", 1));

        private static readonly LootTable Desk = Table(0, 2, 0.35,
            E("tool.batteries", 2, 1, 4), E("med.painkillers", 1), E("food.chocolate", 2), E("tool.scissors", 2),
            E("drink.energy", 2), E("drink.cola", 1), E("tool.lighter", 1));

        private static readonly LootTable FilingCabinet = Table(0, 1, 0.6,
            E("tool.scissors", 1), E("med.rag", 1), E("food.chocolate", 1), E("tool.duct_tape", 1));

        private static readonly LootTable Locker = Table(0, 2, 0.35,
            E("clothing.hoodie", 2), E("clothing.work_boots", 1), E("clothing.gloves", 2), E("drink.energy", 2),
            E("clothing.bike_helmet", 1), E("bag.school", 1), E("weapon.baseball_bat", 0.5), E("tool.torch", 1));

        private static readonly LootTable Bin = Table(0, 1, 0.6,
            E("med.rag", 2), E("food.crisps", 1), E("bag.plastic", 2), E("drink.cola", 1));

        private static readonly LootTable Groceries = Table(2, 6, 0.1,
            E("food.canned_beans", 4), E("food.canned_soup", 4), E("food.canned_tuna", 3), E("food.canned_peaches", 2),
            E("food.pasta", 3), E("food.rice", 2), E("food.cereal", 3), E("food.biscuits", 3), E("food.crisps", 4),
            E("food.chocolate", 3), E("drink.water_bottle", 4), E("drink.water_large", 2), E("drink.cola", 3),
            E("drink.energy", 2), E("tool.batteries", 1, 2, 8), E("bag.plastic", 2));

        private static readonly LootTable ShopStockroom = Table(1, 5, 0.15,
            E("food.rice", 2), E("food.pasta", 2), E("food.canned_beans", 3), E("drink.water_large", 3), E("mat.nails", 1, 20, 80),
            E("mat.rope", 1), E("tool.duct_tape", 2), E("weapon.crowbar", 0.5), E("weapon.hammer", 1), E("bag.duffel", 0.5));

        private static readonly LootTable PharmacyRich = Table(2, 5, 0.1,
            E("med.bandage", 4, 2, 8), E("med.disinfectant", 3), E("med.painkillers", 4), E("med.antibiotics", 2),
            E("med.alcohol_wipes", 3, 5, 15), E("med.suture_needle", 2, 1, 4), E("med.splint", 1));

        private static readonly LootTable PharmacyShop = Table(1, 4, 0.15,
            E("med.bandage", 3, 1, 4), E("med.painkillers", 3), E("med.alcohol_wipes", 2, 2, 8), E("food.chocolate", 2),
            E("drink.water_bottle", 2), E("med.disinfectant", 1), E("tool.batteries", 1, 2, 4));

        private static LootTable Pharmacy(bool rich) => rich ? PharmacyRich : PharmacyShop;
    }

    /// <summary>
    /// Fills one container for one player. Deterministic: the same seed always gives the same
    /// items, so a player's copy of a building can be regenerated rather than stored up front.
    /// </summary>
    public static class LootGenerator
    {
        public static ItemContainer Generate(string seedKey, BuildingKind building, RoomType room, Container container, long todayUnixDay)
        {
            var result = new ItemContainer
            {
                Id = container.Id,
                Name = LootRules.ContainerName(container.Kind),
                Capacity = LootRules.Capacity(container.Kind),
            };

            var rng = new Pcg32(StableHash.Hash64(seedKey));
            var table = LootTables.For(building, room, container.Kind);
            if (table.Entries.Count == 0 || rng.Chance(table.EmptyChance)) return result;

            double total = 0;
            foreach (var e in table.Entries) total += e.Weight;

            int rolls = rng.Range(table.MinRolls, table.MaxRolls + 1);
            for (int i = 0; i < rolls; i++)
            {
                var entry = Pick(table, total, rng);
                var def = ItemCatalog.Get(entry.ItemId);
                var stack = new ItemStack
                {
                    Id = $"{container.Id}#{i}",
                    ItemId = def.Id,
                    Count = def.MaxStack > 1 ? Math.Min(def.MaxStack, rng.Range(entry.MinCount, entry.MaxCount + 1)) : 1,
                    Condition = def.Category == ItemCategory.Weapon || def.Category == ItemCategory.Clothing ? Math.Round(rng.Range(0.3, 1.0), 2) : 1,
                    UsesLeft = def.Uses > 0 ? Math.Max(1, (int)Math.Round(def.Uses * rng.Range(0.3, 1.0))) : 0,
                    // Found food has already been sitting a while.
                    FreshUntilDay = def.Spoils ? todayUnixDay + rng.Range(1, def.FreshDays + 1) : 0,
                };
                InventoryRules.InitBag(stack);
                if (InventoryRules.Weight(stack) > InventoryRules.Free(result)) continue;
                result.Items.Add(stack);
            }
            return result;
        }

        private static LootEntry Pick(LootTable table, double total, Pcg32 rng)
        {
            double r = rng.NextDouble() * total;
            foreach (var e in table.Entries)
            {
                r -= e.Weight;
                if (r < 0) return e;
            }
            return table.Entries[table.Entries.Count - 1];
        }

        /// <summary>Days since 1970-01-01 (UTC), for food freshness.</summary>
        public static long UnixDay(DateTime utc) => (long)Math.Floor((utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays);
    }
}
