using ZombieMap.Core.Buildings;
using ZombieMap.Core.Protocol;
using ZombieMap.Core.Rng;
using Xunit;

namespace ZombieMap.Core.Tests
{
    public class FloorPlanTests
    {
        public static BuildingFootprint Rectangle(string id, BuildingKind kind, double w, double h, double rotationDeg = 0)
        {
            double a = rotationDeg * Math.PI / 180.0;
            Vec2 R(double x, double y) => new Vec2(100 + x * Math.Cos(a) - y * Math.Sin(a), 50 + x * Math.Sin(a) + y * Math.Cos(a));
            return new BuildingFootprint
            {
                BuildingId = id,
                Kind = kind,
                Points = new List<Vec2> { R(0, 0), R(w, 0), R(w, h), R(0, h) },
            };
        }

        public static BuildingFootprint LShape(string id) => new BuildingFootprint
        {
            BuildingId = id,
            Kind = BuildingKind.House,
            Points = new List<Vec2>
            {
                new Vec2(0, 0), new Vec2(14, 0), new Vec2(14, 6), new Vec2(6, 6), new Vec2(6, 12), new Vec2(0, 12),
            },
        };

        [Fact]
        public void Same_building_id_gives_same_plan()
        {
            var a = FloorPlanGenerator.Generate(Rectangle("osm-123", BuildingKind.House, 12, 9));
            var b = FloorPlanGenerator.Generate(Rectangle("osm-123", BuildingKind.House, 12, 9));
            Assert.Equal(ProtocolJson.Serialize(a), ProtocolJson.Serialize(b));
        }

        [Fact]
        public void Different_ids_give_different_plans()
        {
            var plans = Enumerable.Range(0, 10)
                .Select(i => ProtocolJson.Serialize(FloorPlanGenerator.Generate(Rectangle("b" + i, BuildingKind.House, 12, 9)).Rooms))
                .Distinct()
                .Count();
            Assert.True(plans > 1);
        }

        [Theory]
        [InlineData(BuildingKind.House, 12, 9)]
        [InlineData(BuildingKind.Shop, 20, 15)]
        [InlineData(BuildingKind.Pharmacy, 18, 12)]
        [InlineData(BuildingKind.Office, 30, 20)]
        [InlineData(BuildingKind.Station, 60, 25)]
        [InlineData(BuildingKind.Unknown, 8, 6)]
        public void Rooms_fit_do_not_overlap_and_are_all_reachable(BuildingKind kind, double w, double h)
        {
            for (int i = 0; i < 25; i++)
            {
                var plan = FloorPlanGenerator.Generate(Rectangle($"{kind}-{i}", kind, w, h, rotationDeg: i * 13));
                Assert.NotEmpty(plan.Rooms);

                foreach (var r in plan.Rooms)
                {
                    Assert.True(r.X >= -1e-6 && r.Y >= -1e-6, "room starts inside the plan");
                    Assert.True(r.X + r.Width <= plan.Width + 1e-6 && r.Y + r.Height <= plan.Height + 1e-6, "room ends inside the plan");
                    Assert.True(Math.Min(r.Width, r.Height) >= FloorPlanGenerator.MinRoomSize - 1e-6, "room not too thin");
                }

                for (int x = 0; x < plan.Rooms.Count; x++)
                for (int y = x + 1; y < plan.Rooms.Count; y++)
                {
                    var a = plan.Rooms[x];
                    var b = plan.Rooms[y];
                    double ox = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                    double oy = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                    Assert.False(ox > 1e-6 && oy > 1e-6, "rooms overlap");
                }

                Assert.Contains(plan.Doors, d => d.RoomB == Door.Outside && d.RoomA == plan.EntranceRoomId);
                var seen = new HashSet<int> { plan.EntranceRoomId };
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (var d in plan.Doors.Where(d => d.RoomB != Door.Outside))
                    {
                        if (seen.Contains(d.RoomA) && seen.Add(d.RoomB)) grew = true;
                        if (seen.Contains(d.RoomB) && seen.Add(d.RoomA)) grew = true;
                    }
                }
                Assert.Equal(plan.Rooms.Count, seen.Count);
            }
        }

        [Fact]
        public void Rotation_aligns_longest_wall()
        {
            var plan = FloorPlanGenerator.Generate(Rectangle("rot", BuildingKind.House, 15, 8, rotationDeg: 30));
            Assert.Equal(15, Math.Max(plan.Width, plan.Height), 3);
            Assert.Equal(8, Math.Min(plan.Width, plan.Height), 3);
        }

        [Fact]
        public void L_shaped_building_has_no_room_in_the_missing_corner()
        {
            for (int i = 0; i < 20; i++)
            {
                var plan = FloorPlanGenerator.Generate(LShape("L" + i));
                var outline = plan.Outline;
                foreach (var r in plan.Rooms)
                {
                    // The room centre must be inside the L.
                    Assert.True(Inside(r.CenterX, r.CenterY, outline), $"room {r.Id} centre outside outline");
                }
            }
        }

        [Fact]
        public void Houses_get_a_kitchen_and_a_bathroom()
        {
            for (int i = 0; i < 20; i++)
            {
                var plan = FloorPlanGenerator.Generate(Rectangle("house-" + i, BuildingKind.House, 12, 10));
                Assert.Contains(plan.Rooms, r => r.Type == RoomType.Kitchen);
                Assert.Contains(plan.Rooms, r => r.Type == RoomType.Bathroom);
                Assert.Contains(plan.Rooms.Single(r => r.Type == RoomType.Kitchen).Containers, c => c.Kind == ContainerKind.Fridge);
            }
        }

        [Fact]
        public void Container_ids_are_unique()
        {
            var plan = FloorPlanGenerator.Generate(Rectangle("shop", BuildingKind.Shop, 25, 18));
            var ids = plan.Rooms.SelectMany(r => r.Containers).Select(c => c.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }

        [Fact]
        public void Stable_hash_is_stable()
        {
            // Must never change: building layouts depend on it.
            Assert.Equal(StableHash.Hash64("osm-123"), StableHash.Hash64("osm-123"));
            Assert.NotEqual(StableHash.Hash64("osm-123"), StableHash.Hash64("osm-124"));
            Assert.Equal(14695981039346656037UL, StableHash.Hash64(""));
        }

        private static bool Inside(double x, double y, List<Vec2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i];
                var b = poly[j];
                if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }
    }

    public class RandomAndProtocolTests
    {
        [Fact]
        public void Pcg32_is_deterministic_and_in_range()
        {
            var a = new Pcg32(7);
            var b = new Pcg32(7);
            for (int i = 0; i < 1000; i++)
            {
                int x = a.Range(-3, 5);
                Assert.Equal(x, b.Range(-3, 5));
                Assert.InRange(x, -3, 4);
            }
        }

        [Fact]
        public void Envelope_round_trip()
        {
            var json = ProtocolJson.Write(MessageTypes.EncounterAct, new EncounterActRequest
            {
                EncounterId = "e1",
                Action = Encounters.EncounterAction.Attack(Encounters.HitZone.Head),
            }, id: 7);

            Assert.Contains("\"type\":\"encounter.act\"", json);
            Assert.Contains("\"target\":\"Head\"", json);

            var env = ProtocolJson.Read(json);
            Assert.Equal(7, env.Id);
            var req = env.DataAs<EncounterActRequest>();
            Assert.Equal("e1", req.EncounterId);
            Assert.Equal(Encounters.HitZone.Head, req.Action.Target);
        }
    }
}
