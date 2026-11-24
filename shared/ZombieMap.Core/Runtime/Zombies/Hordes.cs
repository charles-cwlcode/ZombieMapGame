using System.Collections.Generic;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Rng;

namespace ZombieMap.Core.Zombies
{
    /// <summary>
    /// A horde: a big slow crowd of shamblers that roams from cell to cell across a region (an H3
    /// resolution-7 hexagon, ~1.2 km across), resting a few minutes at each stop and turning toward
    /// loud noises. On the map it's one marker, like any other group, just bigger.
    /// </summary>
    public sealed class HordeState
    {
        public bool Seeded;
        public int Size;
        public int Dead;
        /// <summary>Its trips, oldest first.</summary>
        public List<MotionLeg> Legs = new List<MotionLeg>();
        /// <summary>Where it rests when it isn't walking.</summary>
        public LatLng RestingAt;
        public string Cell;
        public string PreviousCell;
        public int Steps;
    }

    public static class HordeRules
    {
        public const int MinSize = 15, MaxSize = 35;
        /// <summary>Below this many the horde breaks up (the rest scatter and are gone from the map).</summary>
        public const int BreakUpBelow = 5;
        public const double Speed = 0.3;
        public const long RestMinMs = 2 * 60 * 1000, RestMaxMs = 8 * 60 * 1000;
        /// <summary>Plan the route this far ahead.</summary>
        public const long PlanAheadMs = 30 * 60 * 1000;
        /// <summary>A horde hears noises this far away and turns toward them.</summary>
        public const double HearMetres = 700;
        /// <summary>A horde notices you this close (it's slow, so you can walk away).</summary>
        public const double NoticeMetres = 30;

        /// <summary>How many hordes roam a region: one, sometimes two.</summary>
        public static int HordesIn(string region) =>
            new Pcg32(StableHash.Hash64("hordes|" + region)).Chance(0.4) ? 2 : 1;

        public static string HordeId(string region, int n) => region + ":" + n;

        public static int SizeOf(string hordeId) => new Pcg32(StableHash.Hash64("hordesize|" + hordeId)).Range(MinSize, MaxSize + 1);

        public static string MarkerId(string hordeId) => hordeId + "#";

        public static bool TryParseMarker(string id, out string hordeId)
        {
            hordeId = null;
            if (id == null || !id.EndsWith("#") || id.Length < 2) return false;
            hordeId = id.Substring(0, id.Length - 1);
            return true;
        }

        public static int Alive(HordeState s) => System.Math.Max(0, s.Size - s.Dead);

        public static bool BrokenUp(HordeState s) => s.Seeded && Alive(s) < BreakUpBelow;

        /// <summary>The horde's motion: its trips, resting where the last one ended.</summary>
        public static ZombieMotion Motion(HordeState s) => new ZombieMotion
        {
            Kind = ZombieKind.Shambler,
            Patrol = new Patrol { Loop = new List<LatLng> { s.RestingAt }, Speed = Speed },
            Legs = s.Legs,
        };
    }
}
