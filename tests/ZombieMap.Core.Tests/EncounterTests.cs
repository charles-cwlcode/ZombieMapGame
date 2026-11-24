using ZombieMap.Core.Encounters;
using ZombieMap.Core.Health;
using ZombieMap.Core.Protocol;
using Xunit;

namespace ZombieMap.Core.Tests
{
    public class EncounterTests
    {
        private static Encounter WithZombies(params ZombieSpawn[] spawns) => Encounter.Create(new EncounterConfig
        {
            Seed = 42,
            Arena = ArenaKind.Field,
            Spawns = spawns.ToList(),
        });

        private static ZombieSpawn Z(ZombieKind kind, int lane, int ring, int ticksUntilMove = -1) =>
            new ZombieSpawn { Kind = kind, Lane = lane, Ring = ring, TicksUntilMove = ticksUntilMove };

        [Fact]
        public void Turning_costs_no_ticks()
        {
            var e = WithZombies(Z(ZombieKind.Shambler, 0, 3));
            var r = e.Act(EncounterAction.Turn(1));
            Assert.True(r.Accepted);
            Assert.Equal(0, r.TicksElapsed);
            Assert.Equal(0, e.Tick);
            Assert.Equal(1, e.Survivor.Facing);
        }

        [Theory]
        [InlineData(EncounterActionKind.Wait, 1)]
        [InlineData(EncounterActionKind.PatchHeal, 2)]
        [InlineData(EncounterActionKind.FullHeal, 4)]
        [InlineData(EncounterActionKind.Flee, 2)]
        public void Action_tick_costs_match_design(EncounterActionKind kind, int ticks)
        {
            Assert.Equal(ticks, Rules.BaseTickCost(kind));
        }

        [Fact]
        public void Exhaustion_adds_a_tick_except_turning()
        {
            var s = new Survivor { Endurance = 10 };
            Assert.Equal(5, Rules.TickCost(EncounterActionKind.FullHeal, s));
            Assert.Equal(0, Rules.TickCost(EncounterActionKind.Turn, s));
        }

        [Fact]
        public void Shambler_moves_one_ring_every_two_ticks()
        {
            var e = WithZombies(Z(ZombieKind.Shambler, 0, 3));
            e.Act(EncounterAction.Of(EncounterActionKind.Wait));
            Assert.Equal(3, e.Zombies[0].Ring);
            e.Act(EncounterAction.Of(EncounterActionKind.Wait));
            Assert.Equal(2, e.Zombies[0].Ring);
            e.Act(EncounterAction.Of(EncounterActionKind.Wait));
            e.Act(EncounterAction.Of(EncounterActionKind.Wait));
            Assert.Equal(1, e.Zombies[0].Ring);
        }

        [Fact]
        public void Runner_moves_every_tick()
        {
            var e = WithZombies(Z(ZombieKind.Runner, 0, 3));
            e.Act(EncounterAction.Of(EncounterActionKind.Wait));
            e.Act(EncounterAction.Of(EncounterActionKind.Wait));
            Assert.Equal(1, e.Zombies[0].Ring);
        }

        [Fact]
        public void Patch_heal_stops_bleeding_after_two_quiet_ticks()
        {
            var e = WithZombies(Z(ZombieKind.Shambler, 0, 3));
            e.Survivor.Wounds.Add(new Wound { Part = BodyPart.LeftForearm, Kind = WoundKind.Laceration, Bleeding = true });

            var r = e.Act(EncounterAction.Of(EncounterActionKind.PatchHeal));

            Assert.True(r.Accepted);
            Assert.Equal(2, r.TicksElapsed);
            Assert.False(e.Survivor.Wounds[0].Bleeding);
            Assert.Equal(WoundTreatment.Patched, e.Survivor.Wounds[0].Treatment);
            Assert.Equal(1, e.Survivor.Supplies.Rags);
            Assert.Contains(r.Events, ev => ev.Kind == EncounterEventKind.Healed);
        }

        [Fact]
        public void Full_heal_is_interrupted_when_a_zombie_reaches_you()
        {
            // Shambler at ring 1, moves in 2 ticks: it arrives mid-heal (4 ticks).
            var e = WithZombies(Z(ZombieKind.Shambler, 4, 1, 2));
            e.Survivor.Wounds.Add(new Wound { Part = BodyPart.RightHand, Kind = WoundKind.Scratch, Bleeding = true });

            var r = e.Act(EncounterAction.Of(EncounterActionKind.FullHeal));

            Assert.Contains(r.Events, ev => ev.Kind == EncounterEventKind.Interrupted);
            Assert.NotEqual(WoundTreatment.Treated, e.Survivor.Wounds[0].Treatment);
            Assert.Equal(1, e.Survivor.Supplies.Bandages); // nothing used up
        }

        [Fact]
        public void Successful_push_leaves_zombie_down_for_knockdown_ticks()
        {
            for (ulong seed = 1; seed < 200; seed++)
            {
                var e = Encounter.Create(new EncounterConfig
                {
                    Seed = seed,
                    Spawns = new List<ZombieSpawn> { Z(ZombieKind.Shambler, 0, 1, 5) },
                });
                var r = e.Act(EncounterAction.Of(EncounterActionKind.Push));
                if (!r.Events.Any(ev => ev.Kind == EncounterEventKind.Pushed)) continue;

                Assert.Equal(Rules.KnockdownTicks, e.Zombies[0].KnockedDownTicks);
                return;
            }
            Assert.Fail("No seed produced a successful push.");
        }

        [Fact]
        public void Cannot_flee_down_a_blocked_lane()
        {
            var e = Encounter.Create(new EncounterConfig
            {
                Seed = 1,
                Arena = ArenaKind.Street, // only N and S open
                StartFacing = 2,
                Spawns = new List<ZombieSpawn> { Z(ZombieKind.Shambler, 0, 3) },
            });
            var r = e.Act(EncounterAction.Of(EncounterActionKind.Flee));
            Assert.False(r.Accepted);
            Assert.Equal(0, e.Tick);
        }

        [Fact]
        public void Attack_needs_a_zombie_in_reach()
        {
            var e = WithZombies(Z(ZombieKind.Shambler, 0, 3));
            var r = e.Act(EncounterAction.Attack(HitZone.Head));
            Assert.False(r.Accepted);
        }

        [Fact]
        public void Same_seed_and_actions_give_same_outcome()
        {
            string Play()
            {
                var e = Encounter.Create(new EncounterConfig { Seed = 1234, ZombieCount = 6 });
                for (int i = 0; i < 40 && e.Status == EncounterStatus.Ongoing; i++)
                {
                    var target = e.Zombies.FirstOrDefault(z => !z.Dead && z.Ring <= 1);
                    if (target != null && target.Lane != e.Survivor.Facing)
                        e.Act(EncounterAction.Turn(Lanes.Rotate(target.Lane - e.Survivor.Facing, 0) <= 4
                            ? Lanes.Rotate(target.Lane - e.Survivor.Facing, 0)
                            : Lanes.Rotate(target.Lane - e.Survivor.Facing, 0) - 8));
                    var r = e.Act(EncounterAction.Attack(HitZone.Torso));
                    if (!r.Accepted) e.Act(EncounterAction.Of(EncounterActionKind.Wait));
                }
                return ProtocolJson.Serialize(e);
            }

            Assert.Equal(Play(), Play());
        }

        [Fact]
        public void Encounter_survives_a_json_round_trip()
        {
            var e = Encounter.Create(new EncounterConfig { Seed = 99, ZombieCount = 5 });
            e.Act(EncounterAction.Of(EncounterActionKind.Wait));
            var copy = ProtocolJson.Deserialize<Encounter>(ProtocolJson.Serialize(e));

            var a = e.Act(EncounterAction.Of(EncounterActionKind.Wait));
            var b = copy.Act(EncounterAction.Of(EncounterActionKind.Wait));
            Assert.Equal(ProtocolJson.Serialize(a), ProtocolJson.Serialize(b));
        }

        [Fact]
        public void View_hides_zombies_behind_you_as_sounds()
        {
            var e = WithZombies(
                Z(ZombieKind.Shambler, 0, 2),  // in front
                Z(ZombieKind.Runner, 2, 2),    // side → silhouette
                Z(ZombieKind.Shambler, 4, 1)); // behind → sound
            var v = e.BuildView();

            var front = v.Zombies.Single(z => z.Lane == 0);
            Assert.False(front.Silhouette);
            Assert.Equal(ZombieKind.Shambler, front.Kind);

            var side = v.Zombies.Single(z => z.Lane == 2);
            Assert.True(side.Silhouette);
            Assert.Null(side.Kind);

            Assert.DoesNotContain(v.Zombies, z => z.Lane == 4);
            Assert.Contains(v.Sounds, s => s.Lane == 4 && s.Close);
        }

        [Fact]
        public void Woods_limit_sight_to_ring_one()
        {
            var e = Encounter.Create(new EncounterConfig
            {
                Seed = 3,
                Arena = ArenaKind.Woods,
                Spawns = new List<ZombieSpawn> { Z(ZombieKind.Shambler, 0, 3) },
            });
            var v = e.BuildView();
            Assert.Empty(v.Zombies);
            Assert.Single(v.Sounds);
        }

        [Fact]
        public void Preview_matches_actual_movement()
        {
            var e = WithZombies(Z(ZombieKind.Shambler, 0, 3, 1), Z(ZombieKind.Runner, 1, 3));
            var predicted = EncounterPreview.Predict(e.BuildView(), 3);
            for (int i = 0; i < 3; i++) e.Act(EncounterAction.Of(EncounterActionKind.Wait));
            foreach (var p in predicted)
                Assert.Equal(e.Zombies.Single(z => z.Id == p.Id).Ring, p.Ring);
        }

        [Fact]
        public void Ticks_until_contact_is_correct()
        {
            var e = WithZombies(Z(ZombieKind.Shambler, 0, 2, 1));
            var z = e.BuildView().Zombies.Single();
            // 1 tick to ring 1, then 2 more to ring 0.
            Assert.Equal(3, EncounterPreview.TicksUntilContact(z));
        }

        [Fact]
        public void Bleeding_out_ends_the_encounter()
        {
            var e = WithZombies(Z(ZombieKind.Crawler, 0, 3, 99));
            e.Survivor.Blood = 4;
            e.Survivor.Wounds.Add(new Wound { Part = BodyPart.Neck, Kind = WoundKind.Bite, Bleeding = true });
            e.Act(EncounterAction.Of(EncounterActionKind.Wait));
            e.Act(EncounterAction.Of(EncounterActionKind.Wait));
            Assert.Equal(EncounterStatus.Died, e.Status);
            Assert.Equal("Bled out", e.DeathCause);
        }

        [Fact]
        public void Killing_every_zombie_wins()
        {
            for (ulong seed = 1; seed < 100; seed++)
            {
                var e = Encounter.Create(new EncounterConfig
                {
                    Seed = seed,
                    Spawns = new List<ZombieSpawn> { Z(ZombieKind.Crawler, 0, 1, 99) },
                });
                for (int i = 0; i < 10 && e.Status == EncounterStatus.Ongoing; i++)
                    e.Act(EncounterAction.Attack(HitZone.Torso));
                if (e.Status == EncounterStatus.Won) return;
            }
            Assert.Fail("Never won against a single crawler.");
        }
    }
}
