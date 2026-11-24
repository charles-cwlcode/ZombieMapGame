using System;
using ZombieMap.Core.Health;

namespace ZombieMap.Core.Encounters
{
    /// <summary>
    /// Every tuning number for encounters in one place. First-pass values; expect to tune.
    /// </summary>
    public static class Rules
    {
        public const int MaxRing = 3;
        public const int MeleeRing = 1;

        /// <summary>Ticks a pushed zombie stays down after the push finishes (= one patch heal).</summary>
        public const int KnockdownTicks = 2;

        public const int ExhaustedBelow = 20;
        public const int TiredBelow = 50;

        public const int BloodLossPerBleedingWound = 2;

        public static int BaseTickCost(EncounterActionKind kind)
        {
            switch (kind)
            {
                case EncounterActionKind.Turn: return 0;
                case EncounterActionKind.Attack: return 1;
                case EncounterActionKind.Push: return 1;
                case EncounterActionKind.Stomp: return 1;
                case EncounterActionKind.Wait: return 1;
                case EncounterActionKind.PatchHeal: return 2;
                case EncounterActionKind.FullHeal: return 4;
                case EncounterActionKind.Flee: return 2;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        /// <summary>Exhaustion adds a tick to everything except turning.</summary>
        public static int TickCost(EncounterActionKind kind, Survivor s)
        {
            int cost = BaseTickCost(kind);
            if (cost > 0 && s.Endurance < ExhaustedBelow) cost++;
            return cost;
        }

        public static int EnduranceCost(EncounterActionKind kind)
        {
            switch (kind)
            {
                case EncounterActionKind.Attack: return 6;
                case EncounterActionKind.Push: return 8;
                case EncounterActionKind.Stomp: return 5;
                case EncounterActionKind.Flee: return 15;
                case EncounterActionKind.Wait: return -5; // catch your breath
                default: return 0;
            }
        }

        public static int TicksPerRing(ZombieKind kind)
        {
            switch (kind)
            {
                case ZombieKind.Runner: return 1;
                case ZombieKind.Crawler: return 3;
                default: return 2;
            }
        }

        public static int StartHealth(ZombieKind kind) => kind == ZombieKind.Shambler ? 3 : 2;

        /// <summary>Melee hit chance for a body zone, reduced by tiredness and bleeding arms.</summary>
        public static double HitChance(HitZone zone, Survivor s)
        {
            double chance;
            switch (zone)
            {
                case HitZone.Head: chance = 0.40; break;
                case HitZone.Legs: chance = 0.65; break;
                default: chance = 0.80; break;
            }
            if (s.Endurance < ExhaustedBelow) chance -= 0.15;
            else if (s.Endurance < TiredBelow) chance -= 0.05;

            int bleedingArms = 0;
            foreach (var w in s.Wounds)
            {
                if (!w.Bleeding) continue;
                if (w.Part == BodyPart.LeftHand || w.Part == BodyPart.RightHand ||
                    w.Part == BodyPart.LeftForearm || w.Part == BodyPart.RightForearm)
                    bleedingArms++;
            }
            chance -= 0.05 * Math.Min(bleedingArms, 3);
            return Clamp01(chance);
        }

        public const double HeadKillChance = 0.65;
        public const double StompKillChance = 0.85;

        public static double PushChance(Survivor s, int zombiesInReachOnLane)
        {
            double chance = 0.70 - 0.15 * Math.Max(0, zombiesInReachOnLane - 1);
            if (s.Endurance < ExhaustedBelow) chance -= 0.20;
            else if (s.Endurance < TiredBelow) chance -= 0.05;
            return Clamp01(chance);
        }

        public static double FleeChance(Survivor s, int standingZombiesInReach)
        {
            double chance = 0.80 - 0.10 * standingZombiesInReach;
            foreach (var w in s.Wounds)
                if (BodyParts.IsLeg(w.Part) && w.Treatment != WoundTreatment.Treated) chance -= 0.15;
            if (s.Endurance < ExhaustedBelow) chance -= 0.25;
            return Clamp01(chance);
        }

        /// <summary>Chance a zombie on you (ring 0) lands an attack this tick. Facing it helps you fend it off.</summary>
        public static double GrabHitChance(int laneDistanceFromFacing)
        {
            if (laneDistanceFromFacing == 0) return 0.35;
            if (laneDistanceFromFacing == 1) return 0.45;
            return 0.65;
        }

        public const double WoundScratchChance = 0.55;
        public const double WoundLacerationChance = 0.25; // remainder = bite

        public static double VirusChance(WoundKind kind)
        {
            switch (kind)
            {
                case WoundKind.Bite: return 1.0;
                case WoundKind.Laceration: return 0.25;
                default: return 0.07;
            }
        }

        public const double ScratchBleedChance = 0.5;

        /// <summary>Condition a piece of clothing loses when it stops a hit / when a hit gets through it.</summary>
        public const double ClothingWearOnBlock = 0.08;
        public const double ClothingWearOnHit = 0.04;

        /// <summary>Per tick, with N standing zombies on you, chance to be dragged down = 0.12 × (N − 1).</summary>
        public const double DraggedDownPerExtraZombie = 0.12;

        public const double RunnerSpawnChance = 0.15;
        public const double CrawlerSpawnChance = 0.10;

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }
}
