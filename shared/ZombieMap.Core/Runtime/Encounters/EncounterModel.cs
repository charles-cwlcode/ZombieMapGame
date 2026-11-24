using System;
using System.Collections.Generic;
using ZombieMap.Core.Health;

namespace ZombieMap.Core.Encounters
{
    /// <summary>
    /// The 8 lanes around the survivor. Lane 0 is north and they go clockwise.
    /// </summary>
    public static class Lanes
    {
        public const int Count = 8;
        public static readonly string[] Names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        public static int Rotate(int lane, int steps) => ((lane + steps) % Count + Count) % Count;

        /// <summary>How many 45° steps apart two lanes are (0..4).</summary>
        public static int Distance(int a, int b)
        {
            int d = Math.Abs(a - b) % Count;
            return Math.Min(d, Count - d);
        }

        public static string Name(int lane) => lane >= 0 && lane < Count ? Names[lane] : "?";
    }

    public enum ZombieKind { Shambler, Runner, Crawler }

    public enum ArenaKind { Field, CarPark, Street, Woods }

    public enum HitZone { Head, Torso, Legs }

    public enum EncounterActionKind { Turn, Attack, Push, Stomp, Wait, PatchHeal, FullHeal, Flee }

    public enum EncounterStatus { Ongoing, Won, Fled, Died }

    public sealed class EncounterAction
    {
        public EncounterActionKind Kind;
        /// <summary>Turn only: +1 = 45° clockwise, -1 = 45° anticlockwise. Up to ±4.</summary>
        public int TurnSteps;
        /// <summary>Attack only.</summary>
        public HitZone Target = HitZone.Torso;
        /// <summary>Heals only: index into the survivor's wounds, or -1 for the worst one.</summary>
        public int WoundIndex = -1;

        public static EncounterAction Turn(int steps) => new EncounterAction { Kind = EncounterActionKind.Turn, TurnSteps = steps };
        public static EncounterAction Attack(HitZone zone) => new EncounterAction { Kind = EncounterActionKind.Attack, Target = zone };
        public static EncounterAction Of(EncounterActionKind kind) => new EncounterAction { Kind = kind };
    }

    public sealed class Supplies
    {
        public int Rags = 2;
        public int Bandages = 1;
        public int Disinfectant = 1;

        public Supplies Clone() => new Supplies { Rags = Rags, Bandages = Bandages, Disinfectant = Disinfectant };
    }

    /// <summary>A zombie placed by hand (tests, scripted events). Omit Spawns for random ones.</summary>
    public sealed class ZombieSpawn
    {
        public ZombieKind Kind;
        public int Lane;
        public int Ring = Rules.MaxRing;
        /// <summary>-1 = use the kind's normal speed.</summary>
        public int TicksUntilMove = -1;
    }

    public sealed class EncounterConfig
    {
        public ulong Seed;
        public ArenaKind Arena = ArenaKind.Field;
        public int ZombieCount = 4;
        public int StartFacing;
        /// <summary>Optional: exact zombies instead of random ones.</summary>
        public List<ZombieSpawn> Spawns;
        /// <summary>Optional: overrides the arena's blocked lanes.</summary>
        public List<int> BlockedLanes;
        /// <summary>Optional: overrides the default supplies.</summary>
        public Supplies StartSupplies;
    }

    public sealed class Zombie
    {
        public int Id;
        public ZombieKind Kind;
        public int Lane;
        /// <summary>0 = on you (grabbing), 1 = melee reach, 2 = mid, 3 = far.</summary>
        public int Ring;
        public int Health;
        public int TicksUntilMove;
        public int KnockedDownTicks;
        public bool Dead;

        public bool IsDown => KnockedDownTicks > 0;
        public bool IsStanding => !Dead && KnockedDownTicks == 0;
    }

    public sealed class Survivor
    {
        public int Facing;
        public int Endurance = 100;
        /// <summary>Blood/health pool. Bleeding wounds drain it each tick; 0 = bled out.</summary>
        public int Blood = 100;
        public List<Wound> Wounds = new List<Wound>();
        public Supplies Supplies = new Supplies();
    }

    public enum EncounterEventKind
    {
        Turned, Moved, ReachedYou, Missed, Hit, Killed, Crippled,
        Pushed, PushFailed, GotUp, Stomped,
        GrabMissed, Wounded, Interrupted, Healed,
        Fled, FleeFailed, DraggedDown, BledOut, Won, Rested,
    }

    public sealed class EncounterEvent
    {
        public int Tick;
        public EncounterEventKind Kind;
        public int ZombieId = -1;
        public int Lane = -1;
        public BodyPart? Part;
        public WoundKind? Wound;
        public string Text;
    }

    // ---------- What the client is allowed to see ----------

    public sealed class ZombieView
    {
        public int Id;
        public int Lane;
        public int Ring;
        /// <summary>Side lanes show only a silhouette: kind and timing are hidden (null).</summary>
        public bool Silhouette;
        public ZombieKind? Kind;
        public int? TicksUntilMove;
        public int KnockedDownTicks;
    }

    /// <summary>Something you can hear but not see (behind you, or out of sight range).</summary>
    public sealed class SoundCue
    {
        public int Lane;
        /// <summary>Loud and close (ring 0–1). No exact distance.</summary>
        public bool Close;
    }

    public sealed class WoundView
    {
        public BodyPart Part;
        public WoundKind Kind;
        public bool Bleeding;
        public WoundTreatment Treatment;
    }

    public sealed class EncounterView
    {
        public int Tick;
        public EncounterStatus Status;
        public string DeathCause;
        public ArenaKind Arena;
        public int Facing;
        public int Endurance;
        public int Blood;
        public int VisibleRings;
        public List<int> BlockedLanes = new List<int>();
        public List<ZombieView> Zombies = new List<ZombieView>();
        public List<SoundCue> Sounds = new List<SoundCue>();
        public List<WoundView> Wounds = new List<WoundView>();
        public Supplies Supplies;
        public int ZombiesRemaining;
    }

    public sealed class EncounterResult
    {
        public bool Accepted;
        public string Error;
        public int TicksElapsed;
        public List<EncounterEvent> Events = new List<EncounterEvent>();
        public EncounterView View;
    }
}
