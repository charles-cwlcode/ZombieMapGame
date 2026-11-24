using System;
using System.Collections.Generic;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Items;

namespace ZombieMap.Core.Health
{
    /// <summary>A survivor's body between encounters. Saved with the player.</summary>
    public sealed class BodyState
    {
        /// <summary>0..100. Bleeding and infection drain it; 0 = dead.</summary>
        public int Blood = 100;
        /// <summary>0..100. Fighting uses it; it comes back with time.</summary>
        public int Endurance = 100;
        public List<Wound> Wounds = new List<Wound>();
        public int NextWoundId;

        /// <summary>Unix seconds of the last <see cref="BodyRules.Advance"/>.</summary>
        public long LastUpdated;
        /// <summary>Antibiotics protect from (and clear) wound infection until then.</summary>
        public long AntibioticsUntil;

        /// <summary>0..100, 100 = well fed. Empty: you starve (slowly lose blood).</summary>
        public double Food = 100;
        /// <summary>0..100, 100 = not thirsty. Empty: you dehydrate (lose blood much faster).</summary>
        public double Water = 100;
        /// <summary>0..100, 0 = rested. Climbs while awake; sleeping at your safehouse brings it down.</summary>
        public double Fatigue;
        /// <summary>Sleeping at the safehouse. Any action wakes you.</summary>
        public bool Asleep;

        public bool Dead;
        public string DeathCause;

        // Fractions carried between updates so slow changes aren't lost to rounding.
        public double BloodFraction;
        public double EnduranceFraction;
    }

    public enum TreatAction { BandageWithRag, BandageWithBandage, Disinfect, Stitch, RemoveBandage }

    /// <summary>
    /// How a body changes over real time, and what treating it does. Everything runs in real time,
    /// online or not: an unbandaged wound bleeds while you're logged off, a dirty bandage keeps
    /// festering, and hunger and thirst keep building. Healing and recovery count all time too.
    /// All numbers are tuning, kept here.
    /// </summary>
    public static class BodyRules
    {
        /// <summary>Long gaps are worked through in steps this long, so things change in the right order.</summary>
        public const long StepSeconds = 60;

        public const double BleedSecondsPerBlood = 20;      // one bleeding wound: ~33 min from full to dead
        public const double InfectionSecondsPerBlood = 300; // an infected wound drains slowly
        public const double BloodRegenSecondsPerPoint = 180;
        public const double EnduranceRegenSecondsPerPoint = 6;

        public const long ScratchClotSeconds = 180;         // scratches stop bleeding on their own

        public const long RagDirtySeconds = 2 * 3600;       // a rag bandage needs changing sooner
        public const long BandageDirtySeconds = 8 * 3600;
        /// <summary>Playing time an undisinfected wound (or one under a dirty bandage) takes to get infected.</summary>
        public const double InfectAfterSeconds = 45 * 60;
        public const long AntibioticsSeconds = 12 * 3600;

        // ---- hunger, thirst, sleep ----
        public const double WaterSecondsPerPoint = 24 * 3600 / 100.0;  // full to empty in a day
        public const double FoodSecondsPerPoint = 72 * 3600 / 100.0;   // full to empty in 3 days
        public const double MillilitresPerWaterPoint = 20;             // 2 litres a day
        public const double CaloriesPerFoodPoint = 60;                 // 2000 kcal a day
        public const double DehydrationSecondsPerBlood = 48 * 3600 / 100.0; // empty: dead ~2 days later
        public const double StarvationSecondsPerBlood = 10 * 24 * 3600 / 100.0; // empty: dead ~10 days later
        public const double AwakeSecondsPerFatigue = 16 * 3600 / 100.0;  // fully tired after 16 h awake
        public const double SleepSecondsPerFatigue = 8 * 3600 / 100.0;   // fully rested after 8 h asleep
        /// <summary>Asleep: food and water go down at this rate, wounds heal and endurance comes back faster.</summary>
        public const double SleepNeedsRate = 0.5, SleepHealRate = 1.5, SleepEnduranceRate = 3;
        /// <summary>Tired enough to sleep.</summary>
        public const double SleepMinFatigue = 25;
        /// <summary>Below this, food or water slows endurance recovery.</summary>
        public const double LowNeeds = 15;

        /// <summary>The most endurance you can have at this fatigue (exhaustion caps it).</summary>
        public static int EnduranceCap(double fatigue) => fatigue >= 90 ? 50 : fatigue >= 75 ? 75 : 100;

        public static double HealSeconds(Wound w)
        {
            switch (w.Kind)
            {
                case WoundKind.Scratch: return 2 * 3600;
                case WoundKind.Laceration: return (w.Stitched ? 6 : 12) * 3600;
                default: return 24 * 3600; // bite
            }
        }

        public static bool BandageDirty(Wound w, long now) =>
            w.Bandage != BandageKind.None &&
            now - w.BandagedAt >= (w.Bandage == BandageKind.Rag ? RagDirtySeconds : BandageDirtySeconds);

        /// <summary>Moves the body forward to <paramref name="now"/> (unix seconds).</summary>
        public static void Advance(BodyState b, long now)
        {
            if (b.LastUpdated == 0 || now <= b.LastUpdated)
            {
                if (b.LastUpdated == 0) b.LastUpdated = now;
                return;
            }
            // Months away are worked through in bigger steps; nothing that slow needs the detail.
            long step = now - b.LastUpdated > 30L * 24 * 3600 ? 600 : StepSeconds;
            while (b.LastUpdated < now && !b.Dead)
            {
                long to = Math.Min(now, b.LastUpdated + step);
                Step(b, to, to - b.LastUpdated);
                b.LastUpdated = to;
            }
            b.LastUpdated = now;
        }

        private static void Step(BodyState b, long now, double dt)
        {
            bool antibiotics = now < b.AntibioticsUntil;
            bool thirsty = b.Water <= 0, starving = b.Food <= 0;
            double healRate = (b.Asleep ? SleepHealRate : 1) * (thirsty || starving ? 0.25 : 1);

            int bleeding = 0, infected = 0;
            for (int i = b.Wounds.Count - 1; i >= 0; i--)
            {
                var w = b.Wounds[i];
                if (w.Bleeding && w.Kind == WoundKind.Scratch && now - dt - w.CreatedAt >= ScratchClotSeconds) w.Bleeding = false;

                if (antibiotics) w.Infected = false;
                bool dirty = !w.Disinfected || BandageDirty(w, now);
                if (!w.Infected && dirty && !antibiotics)
                {
                    w.DirtySeconds += dt;
                    if (w.DirtySeconds >= InfectAfterSeconds) w.Infected = true;
                }

                if (w.Bandage != BandageKind.None && !w.Bleeding && !w.Infected)
                    w.Healed = Math.Min(1, w.Healed + dt * healRate / HealSeconds(w));
                if (w.Healed >= 1)
                {
                    b.Wounds.RemoveAt(i);
                    continue;
                }
                if (w.Bleeding) bleeding++;
                if (w.Infected) infected++;
            }

            // Hunger, thirst and tiredness.
            double needs = b.Asleep ? SleepNeedsRate : 1;
            b.Water = Math.Max(0, b.Water - dt * needs / WaterSecondsPerPoint);
            b.Food = Math.Max(0, b.Food - dt * needs / FoodSecondsPerPoint);
            b.Fatigue = b.Asleep
                ? Math.Max(0, b.Fatigue - dt / SleepSecondsPerFatigue)
                : Math.Min(100, b.Fatigue + dt / AwakeSecondsPerFatigue);

            double loss = dt * (bleeding / BleedSecondsPerBlood + infected / InfectionSecondsPerBlood +
                                (thirsty ? 1 / DehydrationSecondsPerBlood : 0) + (starving ? 1 / StarvationSecondsPerBlood : 0));
            if (loss > 0) b.BloodFraction -= loss;
            else if (b.Blood < 100) b.BloodFraction += dt / BloodRegenSecondsPerPoint;
            int whole = (int)Math.Truncate(b.BloodFraction);
            b.Blood = Clamp(b.Blood + whole, 0, 100);
            b.BloodFraction -= whole;
            if (b.Blood == 100 && b.BloodFraction > 0) b.BloodFraction = 0;

            int cap = EnduranceCap(b.Fatigue);
            double enduranceRate = (b.Asleep ? SleepEnduranceRate : 1) * (b.Water < LowNeeds || b.Food < LowNeeds ? 0.5 : 1);
            if (b.Endurance < cap) b.EnduranceFraction += dt * enduranceRate / EnduranceRegenSecondsPerPoint;
            int e = (int)b.EnduranceFraction;
            b.Endurance = Clamp(b.Endurance + e, 0, cap);
            b.EnduranceFraction -= e;
            if (b.Endurance >= cap) b.EnduranceFraction = 0;

            if (b.Blood <= 0)
                Die(b, bleeding > 0 ? "Bled out" : infected > 0 ? "Infection" : thirsty ? "Dehydration" : "Starvation");
        }

        // ---------- food, drink, sleep ----------

        /// <summary>Eats or drinks one of a stack. Returns an error or null.</summary>
        public static string EatOrDrink(BodyState b, PlayerInventory inv, string stackId, long now)
        {
            if (b.Dead) return "You're dead.";
            if (!inv.TryFind(stackId, out var container, out var stack)) return "You don't have that.";
            var def = stack.Def();
            if (def.Category != ItemCategory.Food && def.Category != ItemCategory.Drink) return $"You can't eat {def.Name.ToLowerInvariant()}.";
            if (def.NeedsCooking) return $"{def.Name} needs cooking first.";
            if (stack.IsSpoiled(now / 86400)) return $"The {def.Name.ToLowerInvariant()} has gone off.";

            double food = def.Calories / CaloriesPerFoodPoint, water = def.Hydration / MillilitresPerWaterPoint;
            bool useful = (food > 0 && b.Food < 98) || (water > 0 && b.Water < 98);
            if (!useful) return def.Category == ItemCategory.Drink ? "You're not thirsty." : "You're not hungry.";

            b.Asleep = false;
            b.Food = Math.Min(100, b.Food + food);
            b.Water = Math.Min(100, b.Water + water);
            if (stack.Count > 1) stack.Count--;
            else container.Items.Remove(stack);
            return null;
        }

        /// <summary>Goes to sleep (the caller checks you're at your safehouse). Returns an error or null.</summary>
        public static string Sleep(BodyState b)
        {
            if (b.Dead) return "You're dead.";
            if (b.Asleep) return "You're already asleep.";
            if (b.Wounds.Exists(w => w.Bleeding)) return "You'd bleed out in your sleep. Bandage it first.";
            if (b.Fatigue < SleepMinFatigue) return "You're not tired.";
            b.Asleep = true;
            return null;
        }

        public static void Wake(BodyState b) => b.Asleep = false;

        public static void Die(BodyState b, string cause)
        {
            b.Dead = true;
            b.DeathCause = cause;
        }

        /// <summary>Treat one wound out of combat, using items from the inventory. Returns an error or null.</summary>
        public static string Treat(BodyState b, PlayerInventory inv, int woundId, TreatAction action, long now)
        {
            if (b.Dead) return "You're dead.";
            var w = b.Wounds.Find(x => x.Id == woundId);
            if (w == null) return "That wound has healed.";

            switch (action)
            {
                case TreatAction.BandageWithRag:
                case TreatAction.BandageWithBandage:
                {
                    var use = action == TreatAction.BandageWithRag ? MedicalUse.Rag : MedicalUse.Bandage;
                    if (!InventoryRules.Consume(inv, d => d.Medical == use, 1))
                        return use == MedicalUse.Rag ? "You have no rags." : "You have no bandages.";
                    w.Bandage = use == MedicalUse.Rag ? BandageKind.Rag : BandageKind.Bandage;
                    w.BandagedAt = now;
                    w.Bleeding = false;
                    return null;
                }
                case TreatAction.Disinfect:
                    if (!InventoryRules.Consume(inv, d => d.Medical == MedicalUse.Disinfectant, 1)) return "You have no disinfectant or alcohol wipes.";
                    w.Disinfected = true;
                    w.Infected = false;
                    w.DirtySeconds = 0;
                    return null;
                case TreatAction.Stitch:
                    if (w.Kind != WoundKind.Laceration) return "Only lacerations can be stitched.";
                    if (w.Stitched) return "It's already stitched.";
                    if (!InventoryRules.Consume(inv, d => d.Medical == MedicalUse.SutureNeedle, 1)) return "You have no suture needle.";
                    w.Stitched = true;
                    w.Bleeding = false;
                    return null;
                case TreatAction.RemoveBandage:
                    if (w.Bandage == BandageKind.None) return "There's no bandage on it.";
                    w.Bandage = BandageKind.None;
                    return null;
                default:
                    return "Unknown treatment.";
            }
        }

        /// <summary>Takes one dose of a medicine from a stack. Antibiotics clear and prevent wound infection.</summary>
        public static string TakeMedicine(BodyState b, PlayerInventory inv, string stackId, long now)
        {
            if (b.Dead) return "You're dead.";
            if (!inv.TryFind(stackId, out var container, out var stack)) return "You don't have that.";
            var def = stack.Def();
            if (def.Medical != MedicalUse.Antibiotics) return $"You can't take {def.Name.ToLowerInvariant()} (yet).";

            stack.UsesLeft--;
            if (InventoryRules.Units(stack) <= 0) container.Items.Remove(stack);
            b.AntibioticsUntil = now + AntibioticsSeconds;
            foreach (var w in b.Wounds)
            {
                w.Infected = false;
                w.DirtySeconds = 0;
            }
            return null;
        }

        /// <summary>What the client may see: no zombie-virus truth.</summary>
        public static BodyView ToView(BodyState b, long now)
        {
            var view = new BodyView
            {
                Blood = b.Blood,
                Endurance = b.Endurance,
                Dead = b.Dead,
                DeathCause = b.DeathCause,
                AntibioticsUntil = b.AntibioticsUntil,
                Food = (int)Math.Ceiling(b.Food),
                Water = (int)Math.Ceiling(b.Water),
                Fatigue = (int)Math.Floor(b.Fatigue),
                EnduranceCap = EnduranceCap(b.Fatigue),
                Asleep = b.Asleep,
            };
            foreach (var w in b.Wounds) view.Wounds.Add(ToView(w, now));
            return view;
        }

        public static WoundView ToView(Wound w, long now) => new WoundView
        {
            Id = w.Id,
            Part = w.Part,
            Kind = w.Kind,
            Bleeding = w.Bleeding,
            Treatment = w.Treatment,
            Bandage = w.Bandage,
            BandageDirty = BandageDirty(w, now),
            Disinfected = w.Disinfected,
            Stitched = w.Stitched,
            Infected = w.Infected,
            Healed = Math.Round(w.Healed, 3),
        };

        // ---------- encounters ----------

        /// <summary>Encounter supplies from what's actually in the inventory.</summary>
        public static Supplies SuppliesFrom(PlayerInventory inv) => new Supplies
        {
            Rags = inv.CountOf(d => d.Medical == MedicalUse.Rag),
            Bandages = inv.CountOf(d => d.Medical == MedicalUse.Bandage),
            Disinfectant = Count(inv, d => d.Medical == MedicalUse.Disinfectant),
        };

        /// <summary>Removes from the inventory whatever an encounter used up.</summary>
        public static void ConsumeUsed(PlayerInventory inv, Supplies before, Supplies after)
        {
            InventoryRules.Consume(inv, d => d.Medical == MedicalUse.Rag, Math.Max(0, before.Rags - after.Rags));
            InventoryRules.Consume(inv, d => d.Medical == MedicalUse.Bandage, Math.Max(0, before.Bandages - after.Bandages));
            InventoryRules.Consume(inv, d => d.Medical == MedicalUse.Disinfectant, Math.Max(0, before.Disinfectant - after.Disinfectant));
        }

        /// <summary>Copies an encounter's outcome back onto the body. New wounds get IDs and timestamps.</summary>
        public static void ApplyEncounter(BodyState b, Survivor s, bool died, string deathCause, long now)
        {
            b.Blood = s.Blood;
            b.Endurance = s.Endurance;
            b.Wounds.Clear();
            foreach (var w in s.Wounds)
            {
                var copy = w.Clone();
                if (copy.Id == 0)
                {
                    copy.Id = ++b.NextWoundId;
                    copy.CreatedAt = now;
                }
                if (copy.Bandage != BandageKind.None && copy.BandagedAt == 0) copy.BandagedAt = now;
                b.Wounds.Add(copy);
            }
            if (died) Die(b, deathCause ?? "Killed");
        }

        private static int Count(PlayerInventory inv, Func<ItemDefinition, bool> match)
        {
            int n = 0;
            foreach (var c in new[] { inv.Main, inv.Back?.Contents })
                if (c != null)
                    foreach (var s in c.Items)
                        if (match(s.Def())) n += InventoryRules.Units(s);
            return n;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
    }

    /// <summary>The body as the client sees it.</summary>
    public sealed class BodyView
    {
        public int Blood;
        public int Endurance;
        public bool Dead;
        public string DeathCause;
        public long AntibioticsUntil;
        /// <summary>0..100, 100 = full.</summary>
        public int Food;
        public int Water;
        /// <summary>0..100, 0 = rested.</summary>
        public int Fatigue;
        /// <summary>Most endurance you can have while this tired.</summary>
        public int EnduranceCap = 100;
        public bool Asleep;
        public List<WoundView> Wounds = new List<WoundView>();
    }
}
