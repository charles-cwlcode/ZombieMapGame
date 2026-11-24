namespace ZombieMap.Core.Items
{
    public enum ItemCategory { Food, Drink, Medical, Weapon, Tool, Clothing, Bag, Material }

    /// <summary>What a medical item does. Used by healing (encounters now, health screen later).</summary>
    public enum MedicalUse { None, Rag, Bandage, Disinfectant, Painkillers, Antibiotics, Splint, SutureNeedle }

    /// <summary>
    /// Static data for one kind of item. Instances in the world are <see cref="ItemStack"/>s that
    /// point at a definition by <see cref="Id"/>. All weights are kilograms.
    /// </summary>
    public sealed class ItemDefinition
    {
        /// <summary>Stable ID, e.g. "food.canned_beans". Saved in inventories, so never rename.</summary>
        public string Id;
        public string Name;
        public ItemCategory Category;
        public double Weight;

        /// <summary>How many fit in one stack (rags, nails, batteries). 1 = never stacks.</summary>
        public int MaxStack = 1;

        // ---- food and drink ----
        public int Calories;
        /// <summary>Thirst restored, in millilitres of water equivalent.</summary>
        public int Hydration;
        /// <summary>Days it stays fresh after it's found. 0 = never spoils (tins, dry food).</summary>
        public int FreshDays;

        // ---- medical ----
        public MedicalUse Medical;

        // ---- uses (disinfectant, painkillers, lighter fuel…) ----
        /// <summary>Uses when full. 0 = not a "uses" item.</summary>
        public int Uses;

        // ---- weapons ----
        /// <summary>Melee damage multiplier relative to bare hands (1).</summary>
        public double Damage;
        /// <summary>Hits before it breaks when at full condition. 0 = not a weapon.</summary>
        public int Durability;

        // ---- bags ----
        /// <summary>How much it holds (kg). 0 = not a container.</summary>
        public double Capacity;
        /// <summary>0..1. Contents of an equipped bag weigh this much less (hiking bag 0.6 = 60% lighter).</summary>
        public double WeightReduction;
        /// <summary>Can be worn on the back (frees the hands, gets the weight reduction).</summary>
        public bool WornOnBack;

        public bool IsBag => Capacity > 0;
        public bool Spoils => FreshDays > 0;
    }
}
