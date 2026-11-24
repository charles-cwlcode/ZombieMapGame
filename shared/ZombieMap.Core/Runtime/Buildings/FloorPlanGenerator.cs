using System;
using System.Collections.Generic;
using System.Linq;
using ZombieMap.Core.Rng;

namespace ZombieMap.Core.Buildings
{
    /// <summary>
    /// Turns a building footprint into rooms, doors and loot containers.
    ///
    /// Seeded from the building ID, so the same building always gets the same layout. The
    /// server generates it once and caches it; the client never generates its own (floating
    /// point trig can differ slightly between .NET and IL2CPP).
    ///
    /// Phase 0 algorithm: rotate to the longest wall, BSP-split the bounding box, drop rooms
    /// that fall outside the outline (L-shapes etc.), connect rooms with a random spanning
    /// tree of doors plus a few loops, then name rooms and add containers from templates.
    /// </summary>
    public static class FloorPlanGenerator
    {
        /// <summary>Bump when the algorithm changes so cached plans can be regenerated.</summary>
        public const int Version = 1;

        public const double MinRoomSize = 2.6;
        public const double MinDoorOverlap = 1.0;
        private const double Eps = 1e-6;

        public static FloorPlan Generate(BuildingFootprint footprint)
        {
            if (footprint == null) throw new ArgumentNullException(nameof(footprint));
            var pts = CleanPoints(footprint.Points);
            if (pts.Count < 3) throw new ArgumentException("A footprint needs at least 3 distinct points.");

            var rng = new Pcg32(StableHash.Combine(StableHash.Hash64(footprint.BuildingId ?? ""), Version));
            var plan = new FloorPlan { BuildingId = footprint.BuildingId, Kind = footprint.Kind, GeneratorVersion = Version };

            // 1. Rotate so the longest wall is horizontal, then shift to start at (0,0).
            int longest = 0;
            double bestLen = -1;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                if (len > bestLen) { bestLen = len; longest = i; }
            }
            var pa = pts[longest];
            var pb = pts[(longest + 1) % pts.Count];
            plan.PivotX = pa.X;
            plan.PivotY = pa.Y;
            plan.Rotation = Math.Atan2(pb.Y - pa.Y, pb.X - pa.X);

            double cos = Math.Cos(-plan.Rotation), sin = Math.Sin(-plan.Rotation);
            var local = pts.Select(p =>
            {
                double dx = p.X - pa.X, dy = p.Y - pa.Y;
                return new Vec2(dx * cos - dy * sin, dx * sin + dy * cos);
            }).ToList();
            double minX = local.Min(p => p.X), minY = local.Min(p => p.Y);
            plan.OffsetX = minX;
            plan.OffsetY = minY;
            plan.Outline = local.Select(p => new Vec2(p.X - minX, p.Y - minY)).ToList();
            plan.Width = plan.Outline.Max(p => p.X);
            plan.Height = plan.Outline.Max(p => p.Y);

            // 2. Split the bounding box into rooms.
            double area = PolygonArea(plan.Outline);
            int target = TargetRoomCount(footprint.Kind, area);
            var rects = Split(new Rect(0, 0, plan.Width, plan.Height), target, rng);

            // 3. Keep rooms that are mostly inside the outline.
            var kept = rects.Where(r => InsideFraction(r, plan.Outline) >= 5.0 / 9.0).ToList();
            if (kept.Count == 0) kept.Add(rects.OrderByDescending(r => r.W * r.H).First());

            for (int i = 0; i < kept.Count; i++)
                plan.Rooms.Add(new Room { Id = i, X = kept[i].X, Y = kept[i].Y, Width = kept[i].W, Height = kept[i].H });

            // 4. Doors: entrance, a random spanning tree, then a few extra loops.
            var entrance = plan.Rooms
                .OrderBy(r => r.Y > Eps ? 1 : 0)
                .ThenByDescending(r => r.Y > Eps ? 0 : r.Width)
                .ThenBy(r => r.Y)
                .First();
            plan.EntranceRoomId = entrance.Id;
            plan.Doors.Add(new Door { RoomA = entrance.Id, RoomB = Door.Outside, X = entrance.CenterX, Y = entrance.Y });

            var adjacency = FindAdjacency(plan.Rooms);
            ConnectRooms(plan, adjacency, rng);

            // 5. Drop rooms with no route from the entrance.
            var reachable = Reachable(plan);
            plan.Rooms = plan.Rooms.Where(r => reachable.Contains(r.Id)).ToList();
            plan.Doors = plan.Doors.Where(d => reachable.Contains(d.RoomA) && (d.RoomB == Door.Outside || reachable.Contains(d.RoomB))).ToList();

            // 6. Name rooms and fill them.
            AssignRoomTypes(plan, footprint.Kind);
            foreach (var room in plan.Rooms) AddContainers(room, rng);

            return plan;
        }

        // ---------------------------------------------------------------- geometry

        private struct Rect
        {
            public double X, Y, W, H;
            public Rect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
        }

        private static List<Vec2> CleanPoints(List<Vec2> input)
        {
            var result = new List<Vec2>();
            if (input == null) return result;
            foreach (var p in input)
            {
                if (result.Count > 0 && Close(result[result.Count - 1], p)) continue;
                result.Add(p);
            }
            if (result.Count > 1 && Close(result[0], result[result.Count - 1])) result.RemoveAt(result.Count - 1);
            return result;
        }

        private static bool Close(Vec2 a, Vec2 b) => Math.Abs(a.X - b.X) < 1e-4 && Math.Abs(a.Y - b.Y) < 1e-4;

        private static double PolygonArea(List<Vec2> poly)
        {
            double sum = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(sum) * 0.5;
        }

        private static bool PointInPolygon(double x, double y, List<Vec2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i];
                var b = poly[j];
                if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>Fraction of a 3×3 grid of sample points inside the outline.</summary>
        private static double InsideFraction(Rect r, List<Vec2> poly)
        {
            int inside = 0;
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double x = r.X + r.W * (0.2 + 0.3 * i);
                double y = r.Y + r.H * (0.2 + 0.3 * j);
                if (PointInPolygon(x, y, poly)) inside++;
            }
            return inside / 9.0;
        }

        private static int TargetRoomCount(BuildingKind kind, double area)
        {
            switch (kind)
            {
                case BuildingKind.House: return Clamp((int)Math.Round(area / 14.0), 3, 9);
                case BuildingKind.Shop: return Clamp((int)Math.Round(area / 40.0), 2, 6);
                case BuildingKind.Pharmacy: return Clamp((int)Math.Round(area / 30.0), 3, 7);
                case BuildingKind.Office: return Clamp((int)Math.Round(area / 15.0), 3, 12);
                case BuildingKind.Station: return Clamp((int)Math.Round(area / 60.0), 3, 10);
                default: return Clamp((int)Math.Round(area / 20.0), 2, 8);
            }
        }

        /// <summary>Repeatedly split the largest splittable rectangle along its long side.</summary>
        private static List<Rect> Split(Rect root, int target, Pcg32 rng)
        {
            var leaves = new List<Rect> { root };
            while (leaves.Count < target)
            {
                int pick = -1;
                double bestArea = 0;
                for (int i = 0; i < leaves.Count; i++)
                {
                    var r = leaves[i];
                    if (Math.Max(r.W, r.H) < MinRoomSize * 2) continue;
                    double a = r.W * r.H;
                    if (a > bestArea) { bestArea = a; pick = i; }
                }
                if (pick < 0) break;

                var rect = leaves[pick];
                leaves.RemoveAt(pick);
                bool vertical = rect.W >= rect.H; // split across the long side
                double length = vertical ? rect.W : rect.H;
                double cut = length * rng.Range(0.35, 0.65);
                cut = Math.Max(MinRoomSize, Math.Min(length - MinRoomSize, cut));
                if (vertical)
                {
                    leaves.Add(new Rect(rect.X, rect.Y, cut, rect.H));
                    leaves.Add(new Rect(rect.X + cut, rect.Y, rect.W - cut, rect.H));
                }
                else
                {
                    leaves.Add(new Rect(rect.X, rect.Y, rect.W, cut));
                    leaves.Add(new Rect(rect.X, rect.Y + cut, rect.W, rect.H - cut));
                }
            }
            return leaves;
        }

        private struct Adjacent
        {
            public int A, B;
            public double DoorX, DoorY;
        }

        private static List<Adjacent> FindAdjacency(List<Room> rooms)
        {
            var list = new List<Adjacent>();
            for (int i = 0; i < rooms.Count; i++)
            for (int j = i + 1; j < rooms.Count; j++)
            {
                var a = rooms[i];
                var b = rooms[j];
                // Shared vertical wall.
                if (Math.Abs(a.X + a.Width - b.X) < Eps || Math.Abs(b.X + b.Width - a.X) < Eps)
                {
                    double lo = Math.Max(a.Y, b.Y), hi = Math.Min(a.Y + a.Height, b.Y + b.Height);
                    if (hi - lo >= MinDoorOverlap)
                    {
                        double x = Math.Abs(a.X + a.Width - b.X) < Eps ? b.X : a.X;
                        list.Add(new Adjacent { A = a.Id, B = b.Id, DoorX = x, DoorY = (lo + hi) * 0.5 });
                        continue;
                    }
                }
                // Shared horizontal wall.
                if (Math.Abs(a.Y + a.Height - b.Y) < Eps || Math.Abs(b.Y + b.Height - a.Y) < Eps)
                {
                    double lo = Math.Max(a.X, b.X), hi = Math.Min(a.X + a.Width, b.X + b.Width);
                    if (hi - lo >= MinDoorOverlap)
                    {
                        double y = Math.Abs(a.Y + a.Height - b.Y) < Eps ? b.Y : a.Y;
                        list.Add(new Adjacent { A = a.Id, B = b.Id, DoorX = (lo + hi) * 0.5, DoorY = y });
                    }
                }
            }
            return list;
        }

        private static void ConnectRooms(FloorPlan plan, List<Adjacent> adjacency, Pcg32 rng)
        {
            var connected = new HashSet<int> { plan.EntranceRoomId };
            var used = new HashSet<int>();
            while (true)
            {
                var frontier = new List<int>();
                for (int i = 0; i < adjacency.Count; i++)
                {
                    var e = adjacency[i];
                    if (connected.Contains(e.A) != connected.Contains(e.B)) frontier.Add(i);
                }
                if (frontier.Count == 0) break;
                int pick = frontier[rng.Range(0, frontier.Count)];
                var edge = adjacency[pick];
                used.Add(pick);
                connected.Add(edge.A);
                connected.Add(edge.B);
                plan.Doors.Add(new Door { RoomA = edge.A, RoomB = edge.B, X = edge.DoorX, Y = edge.DoorY });
            }

            // A few extra doors so it isn't a pure tree (more ways to run).
            for (int i = 0; i < adjacency.Count; i++)
            {
                if (used.Contains(i)) continue;
                var edge = adjacency[i];
                if (connected.Contains(edge.A) && connected.Contains(edge.B) && rng.Chance(0.15))
                    plan.Doors.Add(new Door { RoomA = edge.A, RoomB = edge.B, X = edge.DoorX, Y = edge.DoorY });
            }
        }

        private static HashSet<int> Reachable(FloorPlan plan)
        {
            var seen = new HashSet<int> { plan.EntranceRoomId };
            var queue = new Queue<int>();
            queue.Enqueue(plan.EntranceRoomId);
            while (queue.Count > 0)
            {
                int id = queue.Dequeue();
                foreach (var d in plan.Doors)
                {
                    int other = d.RoomA == id ? d.RoomB : d.RoomB == id ? d.RoomA : Door.Outside;
                    if (other != Door.Outside && seen.Add(other)) queue.Enqueue(other);
                }
            }
            return seen;
        }

        // ---------------------------------------------------------------- templates

        private static void AssignRoomTypes(FloorPlan plan, BuildingKind kind)
        {
            var entrance = plan.Rooms.First(r => r.Id == plan.EntranceRoomId);
            var others = plan.Rooms.Where(r => r != entrance).OrderByDescending(r => r.Area).ToList();

            RoomType entranceType;
            RoomType[] largestFirst;   // given to the biggest remaining rooms, in order
            RoomType smallest;         // given to the smallest room (if any left)
            RoomType filler;           // everything else

            switch (kind)
            {
                case BuildingKind.House:
                    entranceType = plan.Rooms.Count >= 5 ? RoomType.Hallway : RoomType.LivingRoom;
                    largestFirst = entranceType == RoomType.Hallway
                        ? new[] { RoomType.LivingRoom, RoomType.Kitchen }
                        : new[] { RoomType.Kitchen };
                    smallest = RoomType.Bathroom;
                    filler = RoomType.Bedroom;
                    break;
                case BuildingKind.Shop:
                    entranceType = RoomType.SalesFloor;
                    largestFirst = new[] { RoomType.Storage, RoomType.Office };
                    smallest = RoomType.Restroom;
                    filler = RoomType.Storage;
                    break;
                case BuildingKind.Pharmacy:
                    entranceType = RoomType.SalesFloor;
                    largestFirst = new[] { RoomType.Dispensary, RoomType.Storage, RoomType.Office };
                    smallest = RoomType.Restroom;
                    filler = RoomType.Storage;
                    break;
                case BuildingKind.Office:
                    entranceType = RoomType.Hallway;
                    largestFirst = new[] { RoomType.Office };
                    smallest = RoomType.Restroom;
                    filler = RoomType.Office;
                    break;
                case BuildingKind.Station:
                    entranceType = RoomType.Concourse;
                    largestFirst = new[] { RoomType.Office, RoomType.Storage };
                    smallest = RoomType.Restroom;
                    filler = RoomType.Room;
                    break;
                default:
                    entranceType = RoomType.Room;
                    largestFirst = new RoomType[0];
                    smallest = RoomType.Storage;
                    filler = RoomType.Room;
                    break;
            }

            entrance.Type = entranceType;
            int next = 0;
            for (int i = 0; i < others.Count; i++)
            {
                bool isSmallest = i == others.Count - 1 && others.Count > largestFirst.Length;
                if (isSmallest) others[i].Type = smallest;
                else if (next < largestFirst.Length) others[i].Type = largestFirst[next++];
                else others[i].Type = filler;
            }
        }

        private struct Slot
        {
            public ContainerKind Kind;
            public int Count;
            public double Chance;
            public Slot(ContainerKind kind, int count, double chance = 1.0) { Kind = kind; Count = count; Chance = chance; }
        }

        private static Slot[] ContainerTemplate(RoomType type)
        {
            switch (type)
            {
                case RoomType.Kitchen: return new[] { new Slot(ContainerKind.Fridge, 1), new Slot(ContainerKind.Cupboard, 2), new Slot(ContainerKind.Counter, 1) };
                case RoomType.LivingRoom: return new[] { new Slot(ContainerKind.Shelf, 1), new Slot(ContainerKind.Drawers, 1, 0.5) };
                case RoomType.Bathroom: return new[] { new Slot(ContainerKind.Cabinet, 1) };
                case RoomType.Bedroom: return new[] { new Slot(ContainerKind.Wardrobe, 1), new Slot(ContainerKind.Drawers, 1) };
                case RoomType.Hallway: return new[] { new Slot(ContainerKind.Closet, 1, 0.5) };
                case RoomType.Storage: return new[] { new Slot(ContainerKind.Shelf, 2), new Slot(ContainerKind.Crate, 1) };
                case RoomType.SalesFloor: return new[] { new Slot(ContainerKind.Shelf, 3), new Slot(ContainerKind.Counter, 1) };
                case RoomType.Dispensary: return new[] { new Slot(ContainerKind.MedicineCabinet, 2), new Slot(ContainerKind.Counter, 1) };
                case RoomType.Office: return new[] { new Slot(ContainerKind.Desk, 1), new Slot(ContainerKind.FilingCabinet, 1) };
                case RoomType.Restroom: return new[] { new Slot(ContainerKind.Cabinet, 1, 0.7) };
                case RoomType.Concourse: return new[] { new Slot(ContainerKind.Bin, 2), new Slot(ContainerKind.Locker, 1) };
                default: return new[] { new Slot(ContainerKind.Crate, 1, 0.5), new Slot(ContainerKind.Shelf, 1, 0.5) };
            }
        }

        private static void AddContainers(Room room, Pcg32 rng)
        {
            int index = 0;
            foreach (var slot in ContainerTemplate(room.Type))
            {
                for (int i = 0; i < slot.Count; i++)
                {
                    if (slot.Chance < 1.0 && !rng.Chance(slot.Chance)) continue;
                    room.Containers.Add(new Container { Id = $"{room.Id}.{index++}", Kind = slot.Kind });
                }
            }
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    }
}
