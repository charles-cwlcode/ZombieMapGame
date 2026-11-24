using System;
using System.Collections.Generic;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Rng;

namespace ZombieMap.Core.Zombies
{
    /// <summary>
    /// A zombie's everyday route: a closed loop of a few points around its home spot, clear of buildings,
    /// walked over and over. Where it is on the loop depends only on the clock, measured from
    /// <see cref="EpochMs"/> (moved whenever the zombie comes back to its loop after going somewhere).
    /// </summary>
    public sealed class Patrol
    {
        public List<LatLng> Loop = new List<LatLng>();
        public long EpochMs;
        public double Speed;
    }

    public enum LegKind { Chase, Drift, Return, Horde }

    /// <summary>One trip: walk a path from a start time, then stand around at the end until <see cref="HoldUntilMs"/>.</summary>
    public sealed class MotionLeg
    {
        public LegKind Kind;
        public List<LatLng> Path = new List<LatLng>();
        public long StartMs;
        public double Speed;
        public long HoldUntilMs;

        [Newtonsoft.Json.JsonIgnore] public double Length => ZombieMotion.PathLength(Path);
        [Newtonsoft.Json.JsonIgnore] public long ArriveMs => StartMs + (long)(Length / Math.Max(0.01, Speed) * 1000);
        [Newtonsoft.Json.JsonIgnore] public long EndMs => Math.Max(ArriveMs, HoldUntilMs);
    }

    /// <summary>
    /// Where a zombie is at any moment: on its patrol loop, unless it's in the middle of trips (a chase,
    /// drifting toward a noise, walking home). Client and server both evaluate it, so they always agree.
    /// </summary>
    public sealed class ZombieMotion
    {
        public ZombieKind Kind;
        public Patrol Patrol = new Patrol();
        /// <summary>Trips in order. When the last one ends the zombie is back on its loop.</summary>
        public List<MotionLeg> Legs = new List<MotionLeg>();
        /// <summary>The player it's chasing, if any (server only).</summary>
        public string Chasing;

        public bool Busy(long nowMs) => Legs.Count > 0 && nowMs < Legs[Legs.Count - 1].EndMs;

        public LatLng PositionAt(long ms)
        {
            if (Legs.Count > 0 && ms >= Legs[0].StartMs)
            {
                for (int i = Legs.Count - 1; i >= 0; i--)
                {
                    var leg = Legs[i];
                    if (ms < leg.StartMs) continue;
                    if (ms < leg.EndMs || i < Legs.Count - 1)
                    {
                        double walked = (ms - leg.StartMs) / 1000.0 * leg.Speed;
                        var p = PointAlong(leg.Path, walked);
                        return ms >= leg.ArriveMs ? Shuffle(p, ms) : p;
                    }
                    break; // last leg done: back on the loop
                }
            }
            return PatrolAt(Patrol, ms);
        }

        /// <summary>Drops trips that are over (keeps the patrol). Server housekeeping.</summary>
        public void Tidy(long nowMs)
        {
            if (Legs.Count > 0 && nowMs >= Legs[Legs.Count - 1].EndMs)
            {
                Legs.Clear();
                Chasing = null;
            }
        }

        public static LatLng PatrolAt(Patrol p, long ms)
        {
            if (p.Loop.Count == 0) return default;
            if (p.Loop.Count == 1) return Shuffle(p.Loop[0], ms);
            double loop = PathLength(p.Loop) + GeoMath.Distance(p.Loop[p.Loop.Count - 1], p.Loop[0]);
            if (loop < 0.01) return p.Loop[0];
            double s = (ms - p.EpochMs) / 1000.0 * p.Speed % loop;
            if (s < 0) s += loop;
            for (int i = 0; i < p.Loop.Count; i++)
            {
                var a = p.Loop[i];
                var b = p.Loop[(i + 1) % p.Loop.Count];
                double d = GeoMath.Distance(a, b);
                if (s <= d) return Lerp(a, b, d < 0.001 ? 0 : s / d);
                s -= d;
            }
            return p.Loop[0];
        }

        public static double PathLength(List<LatLng> path)
        {
            double total = 0;
            for (int i = 1; i < path.Count; i++) total += GeoMath.Distance(path[i - 1], path[i]);
            return total;
        }

        public static LatLng PointAlong(List<LatLng> path, double metres)
        {
            if (path.Count == 0) return default;
            for (int i = 1; i < path.Count; i++)
            {
                double d = GeoMath.Distance(path[i - 1], path[i]);
                if (metres <= d) return Lerp(path[i - 1], path[i], d < 0.001 ? 0 : metres / d);
                metres -= d;
            }
            return path[path.Count - 1];
        }

        private static LatLng Lerp(LatLng a, LatLng b, double t) => new LatLng(a.Lat + (b.Lat - a.Lat) * t, a.Lng + (b.Lng - a.Lng) * t);

        /// <summary>Standing about: a little sway so it doesn't look frozen.</summary>
        private static LatLng Shuffle(LatLng p, long ms)
        {
            double t = ms / 1000.0;
            return GeoMath.Offset(p, 0.8 * Math.Sin(t * 0.37 + p.Lng * 1e5), 0.8 * Math.Cos(t * 0.29 + p.Lat * 1e5));
        }
    }

    /// <summary>How each kind of zombie gets about on the map. Runners are faster than you walk.</summary>
    public static class ZombieSpeeds
    {
        public static double Patrol(ZombieKind k) => k == ZombieKind.Runner ? 0.5 : k == ZombieKind.Crawler ? 0.15 : 0.35;
        public static double Chase(ZombieKind k) => k == ZombieKind.Runner ? 2.6 : k == ZombieKind.Crawler ? 0.4 : 0.9;
        public static double Drift(ZombieKind k) => k == ZombieKind.Runner ? 1.2 : k == ZombieKind.Crawler ? 0.25 : 0.5;
        /// <summary>How close you have to be (in the open, in sight) before it notices you.</summary>
        public static double Notice(ZombieKind k) => k == ZombieKind.Runner ? 35 : k == ZombieKind.Crawler ? 15 : 25;

        /// <summary>The same mix as in fights: 15% runners, 10% crawlers, the rest shamblers. Fixed per zombie.</summary>
        public static ZombieKind KindOf(string zombieKey)
        {
            double roll = new Pcg32(StableHash.Hash64("kind|" + zombieKey)).NextDouble();
            return roll < Rules.RunnerSpawnChance ? ZombieKind.Runner
                : roll < Rules.RunnerSpawnChance + Rules.CrawlerSpawnChance ? ZombieKind.Crawler
                : ZombieKind.Shambler;
        }
    }
}
