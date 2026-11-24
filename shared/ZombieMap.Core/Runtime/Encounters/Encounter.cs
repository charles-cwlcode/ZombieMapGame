using System;
using System.Collections.Generic;
using System.Linq;
using ZombieMap.Core.Health;
using ZombieMap.Core.Items;
using ZombieMap.Core.Rng;

namespace ZombieMap.Core.Encounters
{
    /// <summary>
    /// One zombie encounter: the survivor in the middle, zombies coming in on 8 lanes.
    ///
    /// Time is in ticks and only moves when the player acts: each action costs a fixed
    /// number of ticks (attack 1, patch heal 2, full heal 4…), then the zombies get that
    /// many ticks. The client's shot clock sends Wait when the player hesitates.
    ///
    /// The server owns this object. The client only ever gets an EncounterView.
    /// All randomness comes from Rng, so the same seed and actions give the same result.
    /// </summary>
    public sealed class Encounter
    {
        public EncounterConfig Config;
        public Pcg32 Rng;
        public Survivor Survivor;
        public List<Zombie> Zombies = new List<Zombie>();
        public List<int> BlockedLanes = new List<int>();
        public int VisibleRings = Rules.MaxRing;
        public int Tick;
        public EncounterStatus Status = EncounterStatus.Ongoing;
        public string DeathCause;
        public int NextZombieId = 1;

        public static Encounter Create(EncounterConfig config)
        {
            var e = new Encounter
            {
                Config = config,
                Rng = new Pcg32(config.Seed),
                Survivor = new Survivor
                {
                    Facing = Lanes.Rotate(config.StartFacing, 0),
                    Supplies = (config.StartSupplies ?? new Supplies()).Clone(),
                    Blood = config.StartBlood,
                    Endurance = config.StartEndurance,
                    Wounds = config.StartWounds?.Select(w => w.Clone()).ToList() ?? new List<Wound>(),
                    Worn = config.Worn?.Select(CloneWorn).ToList() ?? new List<ItemStack>(),
                },
            };

            e.BlockedLanes = config.BlockedLanes != null
                ? config.BlockedLanes.Select(l => Lanes.Rotate(l, 0)).Distinct().ToList()
                : DefaultBlockedLanes(config.Arena, e.Rng);
            e.VisibleRings = config.Arena == ArenaKind.Woods ? 1 : Rules.MaxRing;

            if (config.Spawns != null)
            {
                foreach (var s in config.Spawns) e.AddZombie(s.Kind, s.Lane, s.Ring, s.TicksUntilMove);
            }
            else
            {
                var open = Enumerable.Range(0, Lanes.Count).Where(l => !e.BlockedLanes.Contains(l)).ToList();
                int count = config.Kinds?.Count ?? config.ZombieCount;
                for (int i = 0; i < count; i++)
                {
                    double roll = e.Rng.NextDouble();
                    var kind = config.Kinds != null ? config.Kinds[i]
                        : roll < Rules.RunnerSpawnChance ? ZombieKind.Runner
                        : roll < Rules.RunnerSpawnChance + Rules.CrawlerSpawnChance ? ZombieKind.Crawler
                        : ZombieKind.Shambler;
                    int lane = open[e.Rng.Range(0, open.Count)];
                    // From the map: the open lane nearest the direction it really came from.
                    if (config.FromLanes != null && i < config.FromLanes.Count)
                        lane = open.OrderBy(l => Lanes.Distance(l, config.FromLanes[i])).ThenBy(l => l).First();
                    // Stagger arrivals so they don't all land on the same tick.
                    int stagger = Rules.TicksPerRing(kind) + e.Rng.Range(0, 3);
                    e.AddZombie(kind, lane, Rules.MaxRing, stagger);
                }
            }
            return e;
        }

        private static List<int> DefaultBlockedLanes(ArenaKind arena, Pcg32 rng)
        {
            switch (arena)
            {
                case ArenaKind.CarPark:
                {
                    // Parked cars block 2–3 lanes.
                    var lanes = Enumerable.Range(0, Lanes.Count).ToList();
                    int count = rng.Range(2, 4);
                    var blocked = new List<int>();
                    for (int i = 0; i < count; i++)
                    {
                        int idx = rng.Range(0, lanes.Count);
                        blocked.Add(lanes[idx]);
                        lanes.RemoveAt(idx);
                    }
                    blocked.Sort();
                    return blocked;
                }
                case ArenaKind.Street:
                    // An alley: only north and south are open.
                    return new List<int> { 1, 2, 3, 5, 6, 7 };
                default:
                    return new List<int>();
            }
        }

        private void AddZombie(ZombieKind kind, int lane, int ring, int ticksUntilMove)
        {
            Zombies.Add(new Zombie
            {
                Id = NextZombieId++,
                Kind = kind,
                Lane = Lanes.Rotate(lane, 0),
                Ring = System.Math.Max(0, System.Math.Min(Rules.MaxRing, ring)),
                Health = Rules.StartHealth(kind),
                TicksUntilMove = ticksUntilMove > 0 ? ticksUntilMove : Rules.TicksPerRing(kind),
            });
        }

        // ------------------------------------------------------------------ actions

        public EncounterResult Act(EncounterAction action)
        {
            var result = new EncounterResult();
            string error = Validate(action);
            if (error != null)
            {
                result.Accepted = false;
                result.Error = error;
                result.View = BuildView();
                return result;
            }

            result.Accepted = true;
            var events = result.Events;
            var s = Survivor;
            int cost = Rules.TickCost(action.Kind, s);
            s.Endurance = Clamp(s.Endurance - Rules.EnduranceCost(action.Kind), 0, 100);

            // Instant part of the action (resolved at the start of its ticks).
            switch (action.Kind)
            {
                case EncounterActionKind.Turn:
                    s.Facing = Lanes.Rotate(s.Facing, action.TurnSteps);
                    Emit(events, EncounterEventKind.Turned, null, $"You turn to face {Lanes.Name(s.Facing)}.", s.Facing);
                    break;
                case EncounterActionKind.Attack:
                    ResolveAttack(action.Target, events);
                    break;
                case EncounterActionKind.Push:
                    ResolvePush(events, cost);
                    break;
                case EncounterActionKind.Stomp:
                    ResolveStomp(events);
                    break;
                case EncounterActionKind.Wait:
                    Emit(events, EncounterEventKind.Rested, null, "You hesitate and catch your breath.");
                    break;
            }

            // Ticks pass. Healing and fleeing are channelled: a zombie on you interrupts them.
            bool channelled = action.Kind == EncounterActionKind.PatchHeal
                              || action.Kind == EncounterActionKind.FullHeal
                              || action.Kind == EncounterActionKind.Flee;
            bool interrupted = false;
            for (int i = 0; i < cost && Status == EncounterStatus.Ongoing; i++)
            {
                if (AdvanceTick(events)) interrupted = true;
            }
            result.TicksElapsed = cost;

            if (channelled && Status == EncounterStatus.Ongoing)
            {
                if (interrupted)
                    Emit(events, EncounterEventKind.Interrupted, null, $"{Describe(action.Kind)} interrupted: something grabbed you.");
                else
                    CompleteChannelled(action, events);
            }

            if (Status == EncounterStatus.Ongoing && Zombies.All(z => z.Dead))
            {
                Status = EncounterStatus.Won;
                Emit(events, EncounterEventKind.Won, null, "The area is clear.");
            }

            result.View = BuildView();
            return result;
        }

        private string Validate(EncounterAction a)
        {
            if (a == null) return "No action.";
            if (Status != EncounterStatus.Ongoing) return "The encounter is over.";
            var s = Survivor;
            switch (a.Kind)
            {
                case EncounterActionKind.Turn:
                    if (a.TurnSteps == 0 || a.TurnSteps < -4 || a.TurnSteps > 4) return "Turn must be 1–4 steps either way.";
                    return null;
                case EncounterActionKind.Attack:
                    return MeleeTarget(z => !z.Dead) == null ? "Nothing in reach on this lane." : null;
                case EncounterActionKind.Push:
                    return MeleeTarget(z => z.IsStanding) == null ? "Nothing standing in reach on this lane." : null;
                case EncounterActionKind.Stomp:
                    return MeleeTarget(z => !z.Dead && z.IsDown) == null ? "Nothing on the ground in reach on this lane." : null;
                case EncounterActionKind.Wait:
                    return null;
                case EncounterActionKind.PatchHeal:
                    if (FindWound(a.WoundIndex, w => w.Bleeding) == null) return "No bleeding wound to patch.";
                    if (s.Supplies.Rags <= 0 && s.Supplies.Bandages <= 0) return "No rags or bandages.";
                    return null;
                case EncounterActionKind.FullHeal:
                    if (FindWound(a.WoundIndex, w => w.Treatment != WoundTreatment.Treated) == null) return "No wound that needs treating.";
                    if (s.Supplies.Bandages <= 0) return "No clean bandages.";
                    if (s.Supplies.Disinfectant <= 0) return "No disinfectant.";
                    return null;
                case EncounterActionKind.Flee:
                    if (BlockedLanes.Contains(s.Facing)) return "That way is blocked.";
                    if (Zombies.Any(z => z.IsStanding && z.Lane == s.Facing && z.Ring <= Rules.MeleeRing))
                        return "A zombie is in the way.";
                    return null;
                default:
                    return "Unknown action.";
            }
        }

        /// <summary>The closest zombie in melee reach on the lane you face that matches the filter.</summary>
        private Zombie MeleeTarget(System.Func<Zombie, bool> filter) =>
            Zombies.Where(z => !z.Dead && z.Lane == Survivor.Facing && z.Ring <= Rules.MeleeRing && filter(z))
                .OrderBy(z => z.Ring).ThenBy(z => z.Id).FirstOrDefault();

        private Wound FindWound(int index, System.Func<Wound, bool> filter)
        {
            var wounds = Survivor.Wounds;
            if (index >= 0) return index < wounds.Count && filter(wounds[index]) ? wounds[index] : null;
            // Worst first: bleeding, then bites, then lacerations.
            return wounds.Where(filter)
                .OrderByDescending(w => w.Bleeding)
                .ThenByDescending(w => (int)w.Kind)
                .FirstOrDefault();
        }

        private void ResolveAttack(HitZone zone, List<EncounterEvent> events)
        {
            var z = MeleeTarget(x => !x.Dead);
            double chance = Rules.HitChance(zone, Survivor);
            if (!Rng.Chance(chance))
            {
                Emit(events, EncounterEventKind.Missed, z, $"You swing at its {zone.ToString().ToLowerInvariant()} and miss.");
                return;
            }

            switch (zone)
            {
                case HitZone.Head:
                    if (Rng.Chance(Rules.HeadKillChance)) z.Health = 0;
                    else z.Health -= 1;
                    break;
                case HitZone.Legs:
                    if (z.Kind != ZombieKind.Crawler)
                    {
                        z.Kind = ZombieKind.Crawler;
                        z.TicksUntilMove = Rules.TicksPerRing(ZombieKind.Crawler);
                        Emit(events, EncounterEventKind.Crippled, z, "You smash its legs. It drops and crawls.");
                        return;
                    }
                    z.Health -= 1;
                    break;
                default:
                    z.Health -= 1;
                    break;
            }

            if (z.Health <= 0) Kill(z, events, "Your hit drops it for good.");
            else Emit(events, EncounterEventKind.Hit, z, $"Solid hit to the {zone.ToString().ToLowerInvariant()}.");
        }

        private void ResolvePush(List<EncounterEvent> events, int actionCost)
        {
            var z = MeleeTarget(x => x.IsStanding);
            int inReach = Zombies.Count(x => x.IsStanding && x.Lane == Survivor.Facing && x.Ring <= Rules.MeleeRing);
            if (!Rng.Chance(Rules.PushChance(Survivor, inReach)))
            {
                Emit(events, EncounterEventKind.PushFailed, z, "You shove it but it keeps its feet.");
                return;
            }
            // The push's own tick counts down too, so add it: KnockdownTicks remain after the push.
            z.KnockedDownTicks = Rules.KnockdownTicks + actionCost;
            if (z.Ring == 0) z.Ring = 1;
            Emit(events, EncounterEventKind.Pushed, z, "You shove it over!");
        }

        private void ResolveStomp(List<EncounterEvent> events)
        {
            var z = MeleeTarget(x => !x.Dead && x.IsDown);
            if (Rng.Chance(Rules.StompKillChance)) Kill(z, events, "You stomp its skull in.");
            else
            {
                z.Health -= 1;
                if (z.Health <= 0) Kill(z, events, "You stomp it until it stops moving.");
                else Emit(events, EncounterEventKind.Stomped, z, "Your stomp glances off.");
            }
        }

        private void CompleteChannelled(EncounterAction a, List<EncounterEvent> events)
        {
            var s = Survivor;
            switch (a.Kind)
            {
                case EncounterActionKind.PatchHeal:
                {
                    var w = FindWound(a.WoundIndex, x => x.Bleeding);
                    bool rag = s.Supplies.Rags > 0;
                    if (rag) s.Supplies.Rags--; else s.Supplies.Bandages--;
                    w.Bleeding = false;
                    if (w.Bandage == BandageKind.None) w.Bandage = rag ? BandageKind.Rag : BandageKind.Bandage;
                    events.Add(new EncounterEvent { Tick = Tick, Kind = EncounterEventKind.Healed, Part = w.Part, Wound = w.Kind, Text = $"You tie off the {Pretty(w.Part)}. The bleeding stops." });
                    break;
                }
                case EncounterActionKind.FullHeal:
                {
                    var w = FindWound(a.WoundIndex, x => x.Treatment != WoundTreatment.Treated);
                    s.Supplies.Bandages--;
                    s.Supplies.Disinfectant--;
                    w.Bleeding = false;
                    w.Bandage = BandageKind.Bandage;
                    w.BandagedAt = 0; // stamped with real time when the encounter ends
                    w.Disinfected = true;
                    w.Infected = false;
                    w.DirtySeconds = 0;
                    events.Add(new EncounterEvent { Tick = Tick, Kind = EncounterEventKind.Healed, Part = w.Part, Wound = w.Kind, Text = $"You clean and bandage the {Pretty(w.Part)}." });
                    break;
                }
                case EncounterActionKind.Flee:
                {
                    int inReach = Zombies.Count(z => z.IsStanding && z.Ring <= Rules.MeleeRing);
                    if (Rng.Chance(Rules.FleeChance(s, inReach)))
                    {
                        Status = EncounterStatus.Fled;
                        Emit(events, EncounterEventKind.Fled, null, $"You break away to the {Lanes.Name(s.Facing)}.", s.Facing);
                    }
                    else
                    {
                        Emit(events, EncounterEventKind.FleeFailed, null, "You stumble and lose your chance.");
                    }
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ ticks

        /// <summary>Advances one tick. Returns true if a zombie got on you (interrupts channelled actions).</summary>
        private bool AdvanceTick(List<EncounterEvent> events)
        {
            Tick++;
            bool grabbed = false;
            var s = Survivor;

            foreach (var z in Zombies)
            {
                if (z.Dead) continue;

                if (z.KnockedDownTicks > 0)
                {
                    z.KnockedDownTicks--;
                    if (z.KnockedDownTicks == 0) Emit(events, EncounterEventKind.GotUp, z, "It drags itself back up.");
                    continue;
                }

                if (z.Ring > 0)
                {
                    z.TicksUntilMove--;
                    if (z.TicksUntilMove <= 0)
                    {
                        z.Ring--;
                        z.TicksUntilMove = Rules.TicksPerRing(z.Kind);
                        if (z.Ring == 0)
                        {
                            grabbed = true;
                            Emit(events, EncounterEventKind.ReachedYou, z, $"A zombie from the {Lanes.Name(z.Lane)} is on you!");
                        }
                        else
                        {
                            Emit(events, EncounterEventKind.Moved, z, null);
                        }
                    }
                    continue;
                }

                // Ring 0: it's on you and tries to bite or scratch.
                grabbed = true;
                ZombieAttack(z, events);
                if (Status != EncounterStatus.Ongoing) return grabbed;
            }

            // Several zombies on you at once can drag you down.
            int onYou = Zombies.Count(z => z.IsStanding && z.Ring == 0);
            if (onYou >= 2 && Rng.Chance(Rules.DraggedDownPerExtraZombie * (onYou - 1)))
            {
                Die("Dragged down", events, EncounterEventKind.DraggedDown, $"{onYou} of them pull you to the ground.");
                return true;
            }

            // Bleeding.
            int bleeding = s.Wounds.Count(w => w.Bleeding);
            if (bleeding > 0)
            {
                s.Blood -= bleeding * Rules.BloodLossPerBleedingWound;
                if (s.Blood <= 0)
                {
                    s.Blood = 0;
                    Die("Bled out", events, EncounterEventKind.BledOut, "You've lost too much blood.");
                    return true;
                }
            }
            return grabbed;
        }

        private void ZombieAttack(Zombie z, List<EncounterEvent> events)
        {
            int dist = Lanes.Distance(z.Lane, Survivor.Facing);
            if (!Rng.Chance(Rules.GrabHitChance(dist)))
            {
                Emit(events, EncounterEventKind.GrabMissed, z, "Teeth snap shut inches from you.");
                return;
            }

            var part = PickWeighted(BodyParts.GrabHitWeights);
            double roll = Rng.NextDouble();
            var kind = roll < Rules.WoundScratchChance ? WoundKind.Scratch
                : roll < Rules.WoundScratchChance + Rules.WoundLacerationChance ? WoundKind.Laceration
                : WoundKind.Bite;
            if (ClothingBlocks(z, part, kind, events)) return;
            var wound = new Wound
            {
                Part = part,
                Kind = kind,
                Bleeding = kind != WoundKind.Scratch || Rng.Chance(Rules.ScratchBleedChance),
                CarriesVirus = Rng.Chance(Rules.VirusChance(kind)),
            };
            Survivor.Wounds.Add(wound);
            events.Add(new EncounterEvent
            {
                Tick = Tick, Kind = EncounterEventKind.Wounded, ZombieId = z.Id, Lane = z.Lane,
                Part = part, Wound = kind,
                Text = $"{kind} on your {Pretty(part)}!" + (wound.Bleeding ? " It's bleeding." : ""),
            });
        }

        /// <summary>
        /// Clothes covering the hit zone get a chance to stop it, outer layer first. Bites use bite
        /// defence, scratches and lacerations scratch defence, both scaled by condition. Clothes
        /// wear down whether or not they hold.
        /// </summary>
        private bool ClothingBlocks(Zombie z, BodyPart part, WoundKind kind, List<EncounterEvent> events)
        {
            if (Survivor.Worn.Count == 0) return false; // no extra dice rolls without clothes
            foreach (var item in Survivor.Worn.OrderByDescending(i => i.Def().Layer))
            {
                var def = item.Def();
                if (def.Covers == null || Array.IndexOf(def.Covers, part) < 0 || item.Condition <= 0) continue;
                double defense = (kind == WoundKind.Bite ? def.BiteDefense : def.ScratchDefense) * item.Condition;
                bool blocked = Rng.Chance(defense);
                item.Condition = Math.Max(0, Math.Round(item.Condition - (blocked ? Rules.ClothingWearOnBlock : Rules.ClothingWearOnHit), 3));
                if (!blocked) continue;
                events.Add(new EncounterEvent
                {
                    Tick = Tick, Kind = EncounterEventKind.Blocked, ZombieId = z.Id, Lane = z.Lane, Part = part, Wound = kind,
                    Text = $"Your {def.Name.ToLowerInvariant()} stops a {kind.ToString().ToLowerInvariant()} on your {Pretty(part)}." +
                           (item.Condition <= 0 ? " It's torn to shreds." : ""),
                });
                return true;
            }
            return false;
        }

        private static ItemStack CloneWorn(ItemStack s) => new ItemStack
        {
            Id = s.Id, ItemId = s.ItemId, Count = s.Count, Condition = s.Condition, UsesLeft = s.UsesLeft, FreshUntilDay = s.FreshUntilDay,
        };

        private BodyPart PickWeighted(IReadOnlyList<KeyValuePair<BodyPart, int>> weights)
        {
            int total = 0;
            foreach (var kv in weights) total += kv.Value;
            int roll = Rng.Range(0, total);
            foreach (var kv in weights)
            {
                if (roll < kv.Value) return kv.Key;
                roll -= kv.Value;
            }
            return weights[weights.Count - 1].Key;
        }

        private void Kill(Zombie z, List<EncounterEvent> events, string text)
        {
            z.Dead = true;
            z.Health = 0;
            z.KnockedDownTicks = 0;
            Emit(events, EncounterEventKind.Killed, z, text);
        }

        private void Die(string cause, List<EncounterEvent> events, EncounterEventKind kind, string text)
        {
            Status = EncounterStatus.Died;
            DeathCause = cause;
            Emit(events, kind, null, text);
        }

        private void Emit(List<EncounterEvent> events, EncounterEventKind kind, Zombie z, string text, int lane = -1)
        {
            events.Add(new EncounterEvent
            {
                Tick = Tick,
                Kind = kind,
                ZombieId = z?.Id ?? -1,
                Lane = z?.Lane ?? lane,
                Text = text,
            });
        }

        // ------------------------------------------------------------------ view

        /// <summary>
        /// What the player can know. Front three lanes: full detail. Side lanes: silhouettes.
        /// Behind, or beyond sight range: sound cues only. Virus status is never sent.
        /// </summary>
        public EncounterView BuildView()
        {
            var s = Survivor;
            var view = new EncounterView
            {
                Tick = Tick,
                Status = Status,
                DeathCause = DeathCause,
                Arena = Config.Arena,
                Facing = s.Facing,
                Endurance = s.Endurance,
                Blood = s.Blood,
                VisibleRings = VisibleRings,
                BlockedLanes = new List<int>(BlockedLanes),
                Supplies = s.Supplies.Clone(),
                ZombiesRemaining = Zombies.Count(z => !z.Dead),
            };

            var heard = new Dictionary<int, bool>();
            foreach (var z in Zombies)
            {
                if (z.Dead) continue;
                int dist = Lanes.Distance(z.Lane, s.Facing);
                bool inRange = z.Ring <= VisibleRings;
                if (dist <= 1 && inRange)
                {
                    view.Zombies.Add(new ZombieView
                    {
                        Id = z.Id, Lane = z.Lane, Ring = z.Ring, Kind = z.Kind,
                        TicksUntilMove = z.TicksUntilMove, KnockedDownTicks = z.KnockedDownTicks,
                    });
                }
                else if (dist == 2 && inRange)
                {
                    view.Zombies.Add(new ZombieView { Id = z.Id, Lane = z.Lane, Ring = z.Ring, Silhouette = true, KnockedDownTicks = z.KnockedDownTicks });
                }
                else
                {
                    bool close = z.Ring <= Rules.MeleeRing;
                    heard[z.Lane] = heard.TryGetValue(z.Lane, out var c) ? c || close : close;
                }
            }
            foreach (var kv in heard.OrderBy(k => k.Key))
                view.Sounds.Add(new SoundCue { Lane = kv.Key, Close = kv.Value });

            foreach (var w in s.Wounds)
                view.Wounds.Add(BodyRules.ToView(w, 0));

            return view;
        }

        private static string Describe(EncounterActionKind k)
        {
            switch (k)
            {
                case EncounterActionKind.PatchHeal: return "Patching";
                case EncounterActionKind.FullHeal: return "Treating the wound";
                case EncounterActionKind.Flee: return "Running";
                default: return k.ToString();
            }
        }

        private static string Pretty(BodyPart p)
        {
            var name = p.ToString();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i])) sb.Append(' ');
                sb.Append(char.ToLowerInvariant(name[i]));
            }
            return sb.ToString();
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    }
}
