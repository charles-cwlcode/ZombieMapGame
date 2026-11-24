using System;
using System.Collections.Generic;
using ZombieMap.Core.Health;

namespace ZombieMap.Core.Items
{
    /// <summary>
    /// Every item in the game. Phase 1 keeps it in code so tests and both sides always agree;
    /// it can move to a data file once there are hundreds. IDs are saved in inventories: never rename one.
    /// </summary>
    public static class ItemCatalog
    {
        private static readonly Dictionary<string, ItemDefinition> ById = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);

        public static IReadOnlyCollection<ItemDefinition> All => ById.Values;

        public static ItemDefinition Get(string id) =>
            id != null && ById.TryGetValue(id, out var def) ? def : throw new KeyNotFoundException($"Unknown item '{id}'.");

        public static bool TryGet(string id, out ItemDefinition def)
        {
            def = null;
            return id != null && ById.TryGetValue(id, out def);
        }

        static ItemCatalog()
        {
            // ---- food (weights are the whole item) ----
            Food("food.canned_beans", "Tin of beans", 0.45, 380);
            Food("food.canned_soup", "Tin of soup", 0.45, 220);
            Food("food.canned_tuna", "Tin of tuna", 0.2, 190);
            Food("food.canned_peaches", "Tin of peaches", 0.45, 250);
            Food("food.crisps", "Bag of crisps", 0.05, 250, freshDays: 120);
            Food("food.chocolate", "Chocolate bar", 0.1, 520, freshDays: 300);
            Food("food.biscuits", "Packet of biscuits", 0.3, 1400, freshDays: 180);
            Food("food.cereal", "Box of cereal", 0.5, 1900, freshDays: 200);
            Food("food.pasta", "Bag of dried pasta", 0.5, 1800);
            Food("food.rice", "Bag of rice", 1.0, 3600);
            Food("food.bread", "Loaf of bread", 0.8, 2000, freshDays: 4);
            Food("food.apple", "Apple", 0.2, 95, freshDays: 14, hydration: 80);
            Food("food.cheese", "Block of cheese", 0.4, 1600, freshDays: 10);
            Food("food.ready_meal", "Ready meal", 0.4, 600, freshDays: 3);
            Food("food.peanut_butter", "Jar of peanut butter", 0.4, 2300);

            // ---- drink ----
            Drink("drink.water_bottle", "Bottle of water", 0.55, 500);
            Drink("drink.water_large", "Big bottle of water", 2.05, 2000);
            Drink("drink.cola", "Can of cola", 0.35, 300, calories: 140);
            Drink("drink.juice", "Carton of juice", 1.05, 900, calories: 450, freshDays: 7);
            Drink("drink.energy", "Energy drink", 0.27, 220, calories: 110);

            // ---- medical ----
            Add(new ItemDefinition { Id = "med.rag", Name = "Rag", Category = ItemCategory.Medical, Medical = MedicalUse.Rag, Weight = 0.05, MaxStack = 10 });
            Add(new ItemDefinition { Id = "med.bandage", Name = "Bandage", Category = ItemCategory.Medical, Medical = MedicalUse.Bandage, Weight = 0.05, MaxStack = 10 });
            Add(new ItemDefinition { Id = "med.disinfectant", Name = "Bottle of disinfectant", Category = ItemCategory.Medical, Medical = MedicalUse.Disinfectant, Weight = 0.3, Uses = 10 });
            Add(new ItemDefinition { Id = "med.alcohol_wipes", Name = "Alcohol wipes", Category = ItemCategory.Medical, Medical = MedicalUse.Disinfectant, Weight = 0.02, MaxStack = 20 });
            Add(new ItemDefinition { Id = "med.painkillers", Name = "Painkillers", Category = ItemCategory.Medical, Medical = MedicalUse.Painkillers, Weight = 0.05, Uses = 16 });
            Add(new ItemDefinition { Id = "med.antibiotics", Name = "Antibiotics", Category = ItemCategory.Medical, Medical = MedicalUse.Antibiotics, Weight = 0.05, Uses = 7 });
            Add(new ItemDefinition { Id = "med.splint", Name = "Splint", Category = ItemCategory.Medical, Medical = MedicalUse.Splint, Weight = 0.3 });
            Add(new ItemDefinition { Id = "med.suture_needle", Name = "Suture needle", Category = ItemCategory.Medical, Medical = MedicalUse.SutureNeedle, Weight = 0.01, MaxStack = 10 });

            // ---- weapons (Damage vs bare hands = 1) ----
            Weapon("weapon.kitchen_knife", "Kitchen knife", 0.25, 2.0, 30);
            Weapon("weapon.hammer", "Hammer", 0.7, 2.2, 60);
            Weapon("weapon.frying_pan", "Frying pan", 1.4, 2.0, 80);
            Weapon("weapon.baseball_bat", "Baseball bat", 1.0, 2.8, 50);
            Weapon("weapon.crowbar", "Crowbar", 2.0, 3.0, 150);
            Weapon("weapon.golf_club", "Golf club", 0.8, 2.3, 25);

            // ---- tools ----
            Tool("tool.screwdriver", "Screwdriver", 0.25);
            Tool("tool.torch", "Torch", 0.4);
            Add(new ItemDefinition { Id = "tool.batteries", Name = "Battery", Category = ItemCategory.Tool, Weight = 0.03, MaxStack = 12 });
            Add(new ItemDefinition { Id = "tool.lighter", Name = "Lighter", Category = ItemCategory.Tool, Weight = 0.03, Uses = 100 });
            Tool("tool.duct_tape", "Duct tape", 0.3, uses: 10);
            Tool("tool.scissors", "Scissors", 0.15);
            Tool("tool.can_opener", "Tin opener", 0.15);

            // ---- bags ----
            Bag("bag.plastic", "Plastic bag", 0.05, capacity: 5, reduction: 0, back: false);
            Bag("bag.tote", "Tote bag", 0.2, capacity: 8, reduction: 0.1, back: false);
            Bag("bag.school", "School backpack", 0.6, capacity: 12, reduction: 0.3, back: true);
            Bag("bag.duffel", "Duffel bag", 0.9, capacity: 16, reduction: 0.4, back: true);
            Bag("bag.hiking", "Hiking backpack", 1.2, capacity: 20, reduction: 0.6, back: true);

            // ---- clothing: slot, zones covered, scratch / bite defence at full condition ----
            Clothing("clothing.tshirt", "T-shirt", 0.2, ClothingSlot.Torso, 0.10, 0.02, Torso, UpperArms);
            Clothing("clothing.hoodie", "Hoodie", 0.6, ClothingSlot.Jacket, 0.30, 0.08, Torso, UpperArms, Forearms);
            Clothing("clothing.leather_jacket", "Leather jacket", 1.6, ClothingSlot.Jacket, 0.65, 0.35, Torso, UpperArms, Forearms);
            Clothing("clothing.jeans", "Jeans", 0.8, ClothingSlot.Legs, 0.35, 0.12, Legs);
            Clothing("clothing.work_boots", "Work boots", 1.4, ClothingSlot.Feet, 0.90, 0.70, Feet);
            Clothing("clothing.gloves", "Work gloves", 0.15, ClothingSlot.Hands, 0.55, 0.25, Hands);
            Clothing("clothing.bike_helmet", "Bike helmet", 0.35, ClothingSlot.Head, 0.60, 0.50, new[] { BodyPart.Head });
            Clothing("clothing.scarf", "Thick scarf", 0.25, ClothingSlot.Neck, 0.40, 0.20, new[] { BodyPart.Neck });

            // ---- materials ----
            Add(new ItemDefinition { Id = "mat.sheet", Name = "Bed sheet", Category = ItemCategory.Material, Weight = 0.6 });
            Add(new ItemDefinition { Id = "mat.nails", Name = "Nail", Category = ItemCategory.Material, Weight = 0.005, MaxStack = 100 });
            Add(new ItemDefinition { Id = "mat.rope", Name = "Rope", Category = ItemCategory.Material, Weight = 0.5 });
        }

        private static void Add(ItemDefinition def)
        {
            if (ById.ContainsKey(def.Id)) throw new InvalidOperationException($"Duplicate item ID '{def.Id}'.");
            ById.Add(def.Id, def);
        }

        private static void Food(string id, string name, double weight, int calories, int freshDays = 0, int hydration = 0) =>
            Add(new ItemDefinition { Id = id, Name = name, Category = ItemCategory.Food, Weight = weight, Calories = calories, FreshDays = freshDays, Hydration = hydration });

        private static void Drink(string id, string name, double weight, int hydration, int calories = 0, int freshDays = 0) =>
            Add(new ItemDefinition { Id = id, Name = name, Category = ItemCategory.Drink, Weight = weight, Hydration = hydration, Calories = calories, FreshDays = freshDays });

        private static void Weapon(string id, string name, double weight, double damage, int durability) =>
            Add(new ItemDefinition { Id = id, Name = name, Category = ItemCategory.Weapon, Weight = weight, Damage = damage, Durability = durability });

        private static void Tool(string id, string name, double weight, int uses = 0) =>
            Add(new ItemDefinition { Id = id, Name = name, Category = ItemCategory.Tool, Weight = weight, Uses = uses });

        private static void Bag(string id, string name, double weight, double capacity, double reduction, bool back) =>
            Add(new ItemDefinition { Id = id, Name = name, Category = ItemCategory.Bag, Weight = weight, Capacity = capacity, WeightReduction = reduction, WornOnBack = back });

        private static readonly BodyPart[] Torso = { BodyPart.UpperTorso, BodyPart.LowerTorso };
        private static readonly BodyPart[] UpperArms = { BodyPart.LeftUpperArm, BodyPart.RightUpperArm };
        private static readonly BodyPart[] Forearms = { BodyPart.LeftForearm, BodyPart.RightForearm };
        private static readonly BodyPart[] Hands = { BodyPart.LeftHand, BodyPart.RightHand };
        private static readonly BodyPart[] Legs = { BodyPart.Groin, BodyPart.LeftThigh, BodyPart.RightThigh, BodyPart.LeftShin, BodyPart.RightShin };
        private static readonly BodyPart[] Feet = { BodyPart.LeftFoot, BodyPart.RightFoot };

        private static void Clothing(string id, string name, double weight, ClothingSlot slot, double scratch, double bite, params BodyPart[][] covers)
        {
            var zones = new List<BodyPart>();
            foreach (var group in covers) zones.AddRange(group);
            Add(new ItemDefinition
            {
                Id = id, Name = name, Category = ItemCategory.Clothing, Weight = weight,
                Slot = slot, Covers = zones.ToArray(), ScratchDefense = scratch, BiteDefense = bite,
            });
        }
    }
}
