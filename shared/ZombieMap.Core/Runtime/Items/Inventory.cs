using System;
using System.Collections.Generic;

namespace ZombieMap.Core.Items
{
    /// <summary>
    /// One item, or a stack of identical small items, somewhere in the world (a player's pockets,
    /// a bag, a kitchen cupboard). Points at its <see cref="ItemDefinition"/> by <see cref="ItemId"/>.
    /// </summary>
    public sealed class ItemStack
    {
        /// <summary>Unique within whoever holds it (the player's inventory, or one building's loot).</summary>
        public string Id;
        public string ItemId;
        public int Count = 1;

        /// <summary>0..1 for weapons and clothing (1 = new).</summary>
        public double Condition = 1;

        /// <summary>For items with uses (disinfectant, painkillers, lighter). 0 otherwise.</summary>
        public int UsesLeft;

        /// <summary>Unix day it goes off (food). 0 = doesn't spoil.</summary>
        public long FreshUntilDay;

        /// <summary>Bags only: what's inside.</summary>
        public ItemContainer Contents;

        public ItemDefinition Def() => ItemCatalog.Get(ItemId);

        public bool IsSpoiled(long todayUnixDay) => FreshUntilDay > 0 && todayUnixDay > FreshUntilDay;

        /// <summary>Can this be merged into <paramref name="other"/>'s stack?</summary>
        public bool StacksWith(ItemStack other) =>
            other != null && other.ItemId == ItemId && Def().MaxStack > 1 &&
            other.FreshUntilDay == FreshUntilDay && other.UsesLeft == UsesLeft &&
            Math.Abs(other.Condition - Condition) < 1e-9 && Contents == null && other.Contents == null;
    }

    /// <summary>Anything that holds items: pockets, a bag's insides, a cupboard.</summary>
    public sealed class ItemContainer
    {
        public string Id;
        public string Name;
        /// <summary>Kilograms it can hold.</summary>
        public double Capacity;
        public List<ItemStack> Items = new List<ItemStack>();

        public ItemStack Find(string stackId) => Items.Find(s => s.Id == stackId);
    }

    /// <summary>
    /// What a player carries: pockets and hands, plus one bag worn on the back. Weight in a worn
    /// bag counts less (the bag's weight reduction); a bag carried in the hands counts in full.
    /// </summary>
    public sealed class PlayerInventory
    {
        public const string MainId = "main";
        public const string BackId = "back";

        public ItemContainer Main = new ItemContainer { Id = MainId, Name = "Pockets and hands", Capacity = InventoryRules.MainCapacity };

        /// <summary>The bag on your back, or null. Its Contents has Id <see cref="BackId"/>.</summary>
        public ItemStack Back;

        /// <summary>Counter for item IDs given out by this inventory ("i1", "i2"…).</summary>
        public int NextId;

        public string NewId() => "i" + (++NextId);

        /// <summary>"main" or "back" (if a bag is worn); null otherwise.</summary>
        public ItemContainer Container(string id)
        {
            if (id == MainId) return Main;
            if (id == BackId && Back != null) return Back.Contents;
            return null;
        }

        public double CarriedWeight => InventoryRules.Used(Main) + (Back == null ? 0 : InventoryRules.WornWeight(Back));

        public bool HeavyLoad => CarriedWeight > InventoryRules.CarryLimit;

        /// <summary>Finds a stack anywhere the player can reach (pockets or worn bag).</summary>
        public bool TryFind(string stackId, out ItemContainer container, out ItemStack stack)
        {
            foreach (var c in new[] { Main, Back?.Contents })
            {
                if (c == null) continue;
                stack = c.Find(stackId);
                if (stack != null)
                {
                    container = c;
                    return true;
                }
            }
            container = null;
            stack = null;
            return false;
        }

        /// <summary>Total count of an item across pockets and bag (e.g. bandages for healing).</summary>
        public int CountOf(Func<ItemDefinition, bool> match)
        {
            int n = 0;
            foreach (var c in new[] { Main, Back?.Contents })
                if (c != null)
                    foreach (var s in c.Items)
                        if (match(s.Def())) n += s.Count;
            return n;
        }
    }

    /// <summary>Weight and capacity rules. All methods return an error message, or null when it worked.</summary>
    public static class InventoryRules
    {
        /// <summary>Pockets and hands.</summary>
        public const double MainCapacity = 8;

        /// <summary>Above this you're carrying a heavy load (slower, more tiring: phase 1 health/endurance).</summary>
        public const double CarryLimit = 15;

        private const double Epsilon = 1e-9;

        /// <summary>Full weight of a stack, including a bag's contents.</summary>
        public static double Weight(ItemStack s)
        {
            double w = s.Def().Weight * s.Count;
            if (s.Contents != null) w += Used(s.Contents);
            return w;
        }

        /// <summary>Weight of a bag when worn: the bag itself plus its contents less the reduction.</summary>
        public static double WornWeight(ItemStack bag)
        {
            var def = bag.Def();
            return def.Weight + (bag.Contents == null ? 0 : Used(bag.Contents) * (1 - def.WeightReduction));
        }

        public static double Used(ItemContainer c)
        {
            double w = 0;
            foreach (var s in c.Items) w += Weight(s);
            return w;
        }

        public static double Free(ItemContainer c) => Math.Max(0, c.Capacity - Used(c));

        /// <summary>
        /// Moves <paramref name="count"/> of a stack (0 = all) from one container to another, splitting
        /// and merging stacks as needed. New stacks get IDs from <paramref name="newId"/>; if
        /// <paramref name="renameAll"/>, the moved stack is always re-ID'd (items entering a player's
        /// inventory get that inventory's IDs).
        /// </summary>
        public static string Move(ItemContainer from, ItemContainer to, string stackId, int count,
            Func<string> newId, bool renameAll, out ItemStack moved)
        {
            moved = null;
            if (from == null || to == null) return "There's nowhere to put that.";
            if (from == to) return null;
            var stack = from.Find(stackId);
            if (stack == null) return "That item isn't there any more.";
            if (count <= 0 || count > stack.Count) count = stack.Count;

            var def = stack.Def();
            if (stack.Contents != null && stack.Contents.Items.Count > 0 && to.Id == PlayerInventory.BackId)
                return "Empty the " + def.Name.ToLowerInvariant() + " before putting it in another bag.";

            double weight = count == stack.Count ? Weight(stack) : def.Weight * count;
            if (weight > Free(to) + Epsilon)
                return $"Not enough room: needs {weight:0.##} kg, {to.Name} has {Free(to):0.##} kg free.";

            if (count == stack.Count)
            {
                from.Items.Remove(stack);
                moved = stack;
                if (renameAll) moved.Id = newId();
            }
            else
            {
                stack.Count -= count;
                moved = Clone(stack, newId(), count);
            }

            AddMerging(to, moved, newId);
            return null;
        }

        /// <summary>Removes and returns up to <paramref name="count"/> (0 = all) of a stack, e.g. to drop or use it.</summary>
        public static ItemStack Take(ItemContainer from, string stackId, int count, Func<string> newId)
        {
            var stack = from.Find(stackId);
            if (stack == null) return null;
            if (count <= 0 || count >= stack.Count)
            {
                from.Items.Remove(stack);
                return stack;
            }
            stack.Count -= count;
            return Clone(stack, newId(), count);
        }

        /// <summary>Puts a bag from the pockets onto your back, swapping with the one already there.</summary>
        public static string Equip(PlayerInventory inv, string stackId)
        {
            var bag = inv.Main.Find(stackId);
            if (bag == null) return "Pick the bag up first.";
            var def = bag.Def();
            if (!def.IsBag || !def.WornOnBack) return def.Name + " can't be worn on your back.";

            inv.Main.Items.Remove(bag);
            var old = inv.Back;
            if (old != null)
            {
                // The old bag goes to your hands at full weight. Undo if it won't fit.
                if (Weight(old) > Free(inv.Main) + Epsilon)
                {
                    inv.Main.Items.Add(bag);
                    return $"Your hands are too full to hold the {old.Def().Name.ToLowerInvariant()} while you swap.";
                }
                old.Contents.Id = old.Id;
                inv.Main.Items.Add(old);
            }
            bag.Contents ??= new ItemContainer();
            bag.Contents.Id = PlayerInventory.BackId;
            bag.Contents.Name = def.Name;
            bag.Contents.Capacity = def.Capacity;
            inv.Back = bag;
            return null;
        }

        /// <summary>Takes the bag off your back and carries it in your hands.</summary>
        public static string Unequip(PlayerInventory inv)
        {
            var bag = inv.Back;
            if (bag == null) return "You're not wearing a bag.";
            if (Weight(bag) > Free(inv.Main) + Epsilon) return "Your hands are too full to carry it.";
            inv.Back = null;
            bag.Contents.Id = bag.Id;
            inv.Main.Items.Add(bag);
            return null;
        }

        /// <summary>Makes an empty bag item ready to hold things (called when bags are created).</summary>
        public static void InitBag(ItemStack stack)
        {
            var def = stack.Def();
            if (!def.IsBag) return;
            stack.Contents ??= new ItemContainer { Id = stack.Id, Name = def.Name, Capacity = def.Capacity };
        }

        private static void AddMerging(ItemContainer to, ItemStack stack, Func<string> newId)
        {
            int max = stack.Def().MaxStack;
            if (max > 1)
            {
                foreach (var existing in to.Items)
                {
                    if (stack.Count == 0) return;
                    if (!stack.StacksWith(existing) || existing.Count >= max) continue;
                    int n = Math.Min(max - existing.Count, stack.Count);
                    existing.Count += n;
                    stack.Count -= n;
                }
                if (stack.Count == 0) return;
                while (stack.Count > max)
                {
                    to.Items.Add(Clone(stack, newId(), max));
                    stack.Count -= max;
                }
            }
            to.Items.Add(stack);
        }

        private static ItemStack Clone(ItemStack s, string id, int count) => new ItemStack
        {
            Id = id,
            ItemId = s.ItemId,
            Count = count,
            Condition = s.Condition,
            UsesLeft = s.UsesLeft,
            FreshUntilDay = s.FreshUntilDay,
        };
    }
}
