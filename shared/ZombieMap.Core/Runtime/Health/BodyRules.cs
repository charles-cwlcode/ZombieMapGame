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

        public bool Dead;
        public string DeathCause;

        // Fractions carried between updates so slow changes aren't lost to rounding.
        public double BloodFraction;
        public double EnduranceFraction;
    }

    public enum TreatAction { BandageWithRag, BandageWithBandage, Disinfect, Stitch, RemoveBandage }

    /// <summary>
    /// How a body changes over real time, and what treating it does. Harm (bleeding, infection) only
    /// counts time you're playing: gaps longer than <see cref="OnlineGapSeconds"/> between updates
    /// count as offline, where you're sheltered. Healing and recovery count all time.
    /// All numbers are tuning, kept here.
    /// </summary>
    public static class BodyRules
    {
        public const long OnlineGapSeconds = 120;

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
            long elapsed = now - b.LastUpdated;
            double online = Math.Min(elapsed, OnlineGapSeconds);
            double all = elapsed;
            b.LastUpdated = now;
            if (b.Dead) return;

            bool antibiotics = now < b.AntibioticsUntil;
            int bleeding = 0, infected = 0;
            for (int i = b.Wounds.Count - 1; i >= 0; i--)
            {
                var w = b.Wounds[i];
                if (w.Bleeding && w.Kind == WoundKind.Scratch && now - w.CreatedAt >= ScratchClotSeconds) w.Bleeding = false;

                if (antibiotics) w.Infected = false;
                bool dirty = !w.Disinfected || BandageDirty(w, now);
                if (!w.Infected && dirty && !antibiotics)
                {
                    w.DirtySeconds += online;
                    if (w.DirtySeconds >= InfectAfterSeconds) w.Infected = true;
                }

                if (w.Bandage != BandageKind.None && !w.Bleeding && !w.Infected)
                    w.Healed = Math.Min(1, w.Healed + all / HealSeconds(w));
                if (w.Healed >= 1)
                {
                    b.Wounds.RemoveAt(i);
                    continue;
                }
                if (w.Bleeding) bleeding++;
                if (w.Infected) infected++;
            }

            double loss = online * (bleeding / BleedSecondsPerBlood + infected / InfectionSecondsPerBlood);
            b.BloodFraction += loss > 0 ? -loss : b.Blood < 100 ? all / BloodRegenSecondsPerPoint : 0;
            int whole = (int)Math.Truncate(b.BloodFraction);
            b.Blood = Clamp(b.Blood + whole, 0, 100);
            b.BloodFraction -= whole;
            if (b.Blood == 100 && b.BloodFraction > 0) b.BloodFraction = 0;

            b.EnduranceFraction += all / EnduranceRegenSecondsPerPoint;
            int e = (int)b.EnduranceFraction;
            b.Endurance = Clamp(b.Endurance + e, 0, 100);
            b.EnduranceFraction -= e;

            if (b.Blood <= 0) Die(b, bleeding > 0 ? "Bled out" : "Infection");
        }

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
        public List<WoundView> Wounds = new List<WoundView>();
    }
}
