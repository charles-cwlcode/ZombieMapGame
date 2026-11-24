using System;
using System.Collections.Generic;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Rng;

namespace ZombieMap.Core.Zombies
{
    /// <summary>How many buildings of each kind are in a map cell. Sets how many zombies live there.</summary>
    public sealed class CellBuildings
    {
        public int Houses;
        public int Shops;
        public int Pharmacies;
        public int Offices;
        public int Stations;
        public int Other;

        public int Total => Houses + Shops + Pharmacies + Offices + Stations + Other;
    }

    /// <summary>A zombie away from its patrol loop: its trips, and when it gets back.</summary>
    public sealed class ZombieTrip
    {
        public List<MotionLeg> Legs = new List<MotionLeg>();
        /// <summary>Patrol clock after the trip (the zombie rejoins its loop at the loop's first point).</summary>
        public long PatrolEpochMs;
        public string Chasing;
    }

    /// <summary>A group of zombies that moves together: one marker on the map. All one kind.</summary>
    public sealed class ZombieGroup
    {
        public int Index;
        public int Size;
        public ZombieKind Kind;
    }

    /// <summary>
    /// One map cell's zombies over real time, in groups (<see cref="ZombieRules.Groups"/>). Group i of a
    /// cell is the same group for every player: its size, kind, home and loop come from the cell and i.
    /// Each killed zombie adds its group's index to <see cref="Absent"/> until the cell slowly refills;
    /// groups off on a trip are in <see cref="Trips"/>.
    /// </summary>
    public sealed class ZombieCellState
    {
        public bool Seeded;
        public CellBuildings Buildings = new CellBuildings();
        /// <summary>The cell's usual number of zombies (from its buildings).</summary>
        public int Base;
        /// <summary>One entry per dead zombie: the index of the group it belonged to. Oldest first.</summary>
        public List<int> Absent = new List<int>();
        public Dictionary<int, ZombieTrip> Trips = new Dictionary<int, ZombieTrip>();
        public double Noise;
        public long LastUpdated;
        public double RegrowFraction;
    }

    /// <summary>
    /// Zombies on the map: one count per H3 cell (resolution 9, ~350 m across), shaped by the real
    /// buildings in it, refilling over real time. They walk loops around their homes, notice and chase
    /// players, drift toward noise and walk home again, always around buildings. All tuning lives here.
    /// </summary>
    public static class ZombieRules
    {
        /// <summary>A zombie this close starts a fight.</summary>
        public const double EngageMetres = 12;
        /// <summary>Every zombie this close joins the fight.</summary>
        public const double JoinMetres = 35;
        /// <summary>A chasing zombie gives up when you're this far away.</summary>
        public const double LoseMetres = 60;
        public const int MaxInFight = 12;
        public const int MaxPerCell = 40;
        /// <summary>One killed zombie is replaced this often.</summary>
        public const long RegrowSeconds = 20 * 60;
        /// <summary>After a fight on the map, this long before the next one starts by itself (time to get away).</summary>
        public const long FightCooldownSeconds = 45;
        public const double NoiseHalfLifeSeconds = 300;
        /// <summary>Noise from searching and from a fight starting.</summary>
        public const double SearchNoise = 1, FightNoise = 2;
        /// <summary>Share of a cell's idle zombies that one point of noise pulls toward it (at most 60%).</summary>
        public const double DrawPerNoise = 0.15;
        /// <summary>Zombies further than this from a noise don't hear it.</summary>
        public const double HearMetres = 450;
        /// <summary>How long zombies hang about where they heard a noise before wandering home.</summary>
        public const long LingerMinMs = 3 * 60 * 1000, LingerMaxMs = 6 * 60 * 1000;
        /// <summary>A zombie that caught up with you stands there this long before giving up.</summary>
        public const long ChaseHoldMs = 6000;
        /// <summary>Each cell's grid of buildings covers the cell and this much around it (for chases and noise).</summary>
        public const double GridMarginMetres = 250;

        /// <summary>The cell's usual zombie count: busier places have more. Varies ±30% per cell.</summary>
        public static int BaseCount(string cell, CellBuildings b)
        {
            double n = 1 + 0.06 * b.Houses + 0.6 * b.Shops + 0.8 * b.Pharmacies + 0.3 * b.Offices + 4 * b.Stations + 0.03 * b.Other;
            var rng = new Pcg32(StableHash.Hash64("zombies|" + cell));
            n *= rng.Range(0.7, 1.3);
            return Math.Max(0, Math.Min(MaxPerCell, (int)Math.Round(n)));
        }

        /// <summary>Where a fight happens: built-up cells are streets, empty ones open ground.</summary>
        public static ArenaKind ArenaFor(CellBuildings b) =>
            b.Total >= 60 ? ArenaKind.Street : b.Total >= 15 ? ArenaKind.CarPark : ArenaKind.Field;

        // ---------- state over time ----------

        public static void Seed(ZombieCellState s, string cell, CellBuildings buildings, long now)
        {
            s.Seeded = true;
            s.Buildings = buildings;
            s.Base = BaseCount(cell, buildings);
            s.LastUpdated = now;
        }

        public static void Advance(ZombieCellState s, long now)
        {
            if (s.LastUpdated == 0 || now <= s.LastUpdated)
            {
                if (s.LastUpdated == 0) s.LastUpdated = now;
                return;
            }
            double elapsed = now - s.LastUpdated;
            s.LastUpdated = now;
            s.Noise *= Math.Pow(0.5, elapsed / NoiseHalfLifeSeconds);
            if (s.Noise < 0.01) s.Noise = 0;

            s.RegrowFraction = s.Absent.Count > 0 ? s.RegrowFraction + elapsed / RegrowSeconds : 0;
            while (s.RegrowFraction >= 1 && s.Absent.Count > 0)
            {
                s.Absent.RemoveAt(0);
                s.RegrowFraction -= 1;
            }

            var done = new List<int>();
            foreach (var kv in s.Trips)
                if (kv.Value.Legs.Count == 0 || now * 1000 >= kv.Value.Legs[kv.Value.Legs.Count - 1].EndMs) done.Add(kv.Key);
            // Finished trips keep only their patrol clock, so the zombie carries on round its loop from where it rejoined.
            foreach (int i in done)
            {
                var t = s.Trips[i];
                t.Legs.Clear();
                t.Chasing = null;
            }
        }

        /// <summary>
        /// How the cell's zombies are split into groups: lone stragglers (30%), small groups of 2–3 (35%),
        /// 4–6 (25%) and crowds of 7–12 (10%). Runner packs are at most 6. Fixed per cell.
        /// </summary>
        public static List<ZombieGroup> Groups(string cell, int total)
        {
            var rng = new Pcg32(StableHash.Hash64("groups|" + cell));
            var groups = new List<ZombieGroup>();
            for (int left = total, g = 0; left > 0; g++)
            {
                double r = rng.NextDouble();
                int size = r < 0.30 ? 1 : r < 0.65 ? rng.Range(2, 4) : r < 0.90 ? rng.Range(4, 7) : rng.Range(7, 13);
                var kind = KindOf(cell, g);
                if (kind == ZombieKind.Runner) size = Math.Min(size, 6);
                size = Math.Min(size, left);
                groups.Add(new ZombieGroup { Index = g, Size = size, Kind = kind });
                left -= size;
            }
            return groups;
        }

        /// <summary>How many of each group are alive now (groups with none left are missing).</summary>
        public static Dictionary<int, int> Alive(ZombieCellState s, List<ZombieGroup> groups)
        {
            var alive = new Dictionary<int, int>();
            foreach (var g in groups) alive[g.Index] = g.Size;
            foreach (int g in s.Absent)
                if (alive.ContainsKey(g)) alive[g]--;
            var gone = new List<int>();
            foreach (var kv in alive) if (kv.Value <= 0) gone.Add(kv.Key);
            foreach (int g in gone) alive.Remove(g);
            return alive;
        }

        public static int AliveTotal(ZombieCellState s) => Math.Max(0, s.Base - s.Absent.Count);

        /// <summary>Kills up to <paramref name="count"/> of a group. A group wiped out comes back home when it refills.</summary>
        public static int Kill(ZombieCellState s, List<ZombieGroup> groups, int group, int count)
        {
            Alive(s, groups).TryGetValue(group, out int alive);
            int n = Math.Min(alive, count);
            for (int k = 0; k < n; k++) s.Absent.Add(group);
            if (n > 0 && n == alive) s.Trips.Remove(group);
            return n;
        }

        /// <summary>How many of <paramref name="idle"/> groups noise of this size pulls over.</summary>
        public static int DrawnBy(double noise, int idle) =>
            Math.Min(idle, (int)Math.Floor(idle * Math.Min(0.6, noise * DrawPerNoise)));

        // ---------- each zombie ----------

        public static string MarkerId(string cell, int index) => cell + ":" + index;

        public static bool TryParseMarker(string id, out string cell, out int index)
        {
            cell = null;
            index = -1;
            if (id == null || id.IndexOf('#') >= 0) return false; // a horde member
            int colon = id.LastIndexOf(':');
            if (colon <= 0) return false;
            cell = id.Substring(0, colon);
            return int.TryParse(id.Substring(colon + 1), out index);
        }

        public static ZombieKind KindOf(string cell, int index) => ZombieSpeeds.KindOf(MarkerId(cell, index));

        /// <summary>A zombie's home spot: a fixed random point inside the cell, on open ground.</summary>
        public static LatLng Home(string cell, int index, IReadOnlyList<LatLng> outline, ObstacleGrid grid)
        {
            var rng = new Pcg32(StableHash.Hash64($"home|{cell}|{index}"));
            double minLat = double.MaxValue, maxLat = double.MinValue, minLng = double.MaxValue, maxLng = double.MinValue;
            foreach (var p in outline)
            {
                minLat = Math.Min(minLat, p.Lat); maxLat = Math.Max(maxLat, p.Lat);
                minLng = Math.Min(minLng, p.Lng); maxLng = Math.Max(maxLng, p.Lng);
            }
            LatLng inside = GeoMath.Centre(outline);
            bool foundInside = false;
            for (int tries = 0; tries < 60; tries++)
            {
                var p = new LatLng(rng.Range(minLat, maxLat), rng.Range(minLng, maxLng));
                if (!Inside(p, outline)) continue;
                if (!foundInside) { inside = p; foundInside = true; }
                if (grid == null || grid.IsFree(p)) return p;
            }
            return grid != null && grid.NearestFree(inside, 40, out var free) ? free : inside;
        }

        /// <summary>
        /// The zombie's loop: 3–5 points round its home, each reachable from the last in a straight line
        /// without going through a building. In a tight spot it ends up standing about instead.
        /// </summary>
        public static Patrol PatrolFor(string cell, int index, IReadOnlyList<LatLng> outline, ObstacleGrid grid)
        {
            var kind = KindOf(cell, index);
            var home = Home(cell, index, outline, grid);
            var rng = new Pcg32(StableHash.Hash64($"loop|{cell}|{index}"));
            int points = rng.Range(3, 6);
            double maxR = kind == ZombieKind.Crawler ? 8 : 20;
            double start = rng.Range(0, Math.PI * 2);
            double dir = rng.Chance(0.5) ? 1 : -1;
            var loop = new List<LatLng> { home };
            for (int k = 1; k < points; k++)
            {
                double a = start + dir * k * Math.PI * 2 / points + rng.Range(-0.3, 0.3);
                double r = rng.Range(maxR * 0.3, maxR);
                for (int tries = 0; tries < 3; tries++, r *= 0.5)
                {
                    var p = GeoMath.Offset(home, Math.Cos(a) * r, Math.Sin(a) * r);
                    if (grid == null || (grid.IsFree(p) && grid.LineClear(loop[loop.Count - 1], p)))
                    {
                        loop.Add(p);
                        break;
                    }
                }
            }
            while (loop.Count > 2 && grid != null && !grid.LineClear(loop[loop.Count - 1], loop[0])) loop.RemoveAt(loop.Count - 1);
            if (loop.Count == 2 && grid != null && !grid.LineClear(loop[1], loop[0])) loop.RemoveAt(1);
            // Every zombie starts at a different point on its loop.
            long epoch = -(long)(rng.NextDouble() * 600_000);
            return new Patrol { Loop = loop, EpochMs = epoch, Speed = ZombieSpeeds.Patrol(kind) };
        }

        /// <summary>The zombie's motion: its loop, plus any trip it's on.</summary>
        public static ZombieMotion MotionFor(string cell, int index, Patrol patrol, ZombieTrip trip)
        {
            var m = new ZombieMotion { Kind = KindOf(cell, index), Patrol = patrol };
            if (trip != null)
            {
                m.Patrol = new Patrol { Loop = patrol.Loop, Speed = patrol.Speed, EpochMs = trip.PatrolEpochMs };
                m.Legs = trip.Legs;
                m.Chasing = trip.Chasing;
            }
            return m;
        }

        // ---------- trips ----------

        /// <summary>
        /// Chase a player: run (or shuffle) toward them around buildings, stand there a moment, then walk
        /// back to the loop. Null if there's no way through.
        /// </summary>
        public static ZombieTrip Chase(ZombieMotion m, ObstacleGrid grid, LatLng target, long nowMs, string playerId)
        {
            var from = m.PositionAt(nowMs);
            var path = grid.FindPath(from, target);
            if (path == null) return null;
            var leg = new MotionLeg { Kind = LegKind.Chase, Path = path, StartMs = nowMs, Speed = ZombieSpeeds.Chase(m.Kind) };
            leg.HoldUntilMs = leg.ArriveMs + ChaseHoldMs;
            var trip = new ZombieTrip { Chasing = playerId };
            trip.Legs.Add(leg);
            AddReturn(trip, m, grid);
            return trip;
        }

        /// <summary>Head toward a noise, hang about there a few minutes, then wander home.</summary>
        public static ZombieTrip Drift(ZombieMotion m, ObstacleGrid grid, LatLng noise, long nowMs, Pcg32 rng)
        {
            var from = m.PositionAt(nowMs);
            var spot = GeoMath.Offset(noise, rng.Range(-12, 12), rng.Range(-12, 12));
            var path = grid.FindPath(from, spot);
            if (path == null) return null;
            var leg = new MotionLeg { Kind = LegKind.Drift, Path = path, StartMs = nowMs, Speed = ZombieSpeeds.Drift(m.Kind) };
            leg.HoldUntilMs = leg.ArriveMs + (long)rng.Range(LingerMinMs, LingerMaxMs);
            var trip = new ZombieTrip();
            trip.Legs.Add(leg);
            AddReturn(trip, m, grid);
            return trip;
        }

        /// <summary>Give up whatever it was doing and walk straight home from where it is now.</summary>
        public static ZombieTrip GoHome(ZombieMotion m, ObstacleGrid grid, long nowMs)
        {
            var trip = new ZombieTrip();
            var from = m.PositionAt(nowMs);
            var path = grid.FindPath(from, m.Patrol.Loop[0]) ?? new List<LatLng> { from, m.Patrol.Loop[0] };
            var leg = new MotionLeg { Kind = LegKind.Return, Path = path, StartMs = nowMs, Speed = m.Patrol.Speed * 1.5 };
            leg.HoldUntilMs = leg.ArriveMs;
            trip.Legs.Add(leg);
            trip.PatrolEpochMs = leg.ArriveMs;
            return trip;
        }

        private static void AddReturn(ZombieTrip trip, ZombieMotion m, ObstacleGrid grid)
        {
            var last = trip.Legs[trip.Legs.Count - 1];
            var end = last.Path[last.Path.Count - 1];
            var home = m.Patrol.Loop[0];
            var path = grid.FindPath(end, home) ?? new List<LatLng> { end, home };
            var back = new MotionLeg { Kind = LegKind.Return, Path = path, StartMs = last.EndMs, Speed = m.Patrol.Speed * 1.5 };
            back.HoldUntilMs = back.ArriveMs;
            trip.Legs.Add(back);
            trip.PatrolEpochMs = back.ArriveMs;
        }

        /// <summary>Point-in-polygon on lat/lng (fine for cells a few hundred metres across).</summary>
        public static bool Inside(LatLng p, IReadOnlyList<LatLng> ring)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                if ((a.Lat > p.Lat) != (b.Lat > p.Lat) &&
                    p.Lng < (b.Lng - a.Lng) * (p.Lat - a.Lat) / (b.Lat - a.Lat) + a.Lng)
                    inside = !inside;
            }
            return inside;
        }
    }

    /// <summary>A group of zombies on the map, as the client sees it: how many, what kind, and how it moves.</summary>
    public sealed class ZombieMarker
    {
        /// <summary>"{cell}:{group}" or "{horde}#": the same group for every player.</summary>
        public string Id;
        public ZombieKind Kind;
        public int Count;
        public bool Horde;
        /// <summary>Where it is at the reply's time (the client works out later positions from <see cref="Motion"/>).</summary>
        public LatLng Position;
        public ZombieMotion Motion;
        /// <summary>True while it's coming for you.</summary>
        public bool Chasing;
    }

    /// <summary>One map cell, as the client sees it: its outline and how many zombies are in it.</summary>
    public sealed class ZombieCellView
    {
        public string Cell;
        public List<LatLng> Outline = new List<LatLng>();
        public int Zombies;
        public double Noise;
    }
}
