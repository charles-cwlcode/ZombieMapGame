using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ZombieMap.Client.Net;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Health;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Client.Dev
{
    /// <summary>
    /// Phase 0 encounter prototype in IMGUI: 8 lanes around you, facing is always "up".
    /// Hover an action to see ghost positions after its ticks. Shot clock sends Wait if you
    /// hesitate. Swipe / A-D / arrow keys turn. Ugly on purpose: it's for feeling out the rules.
    /// </summary>
    public sealed class EncounterDebugPanel
    {
        private readonly GameClient _client;
        private readonly List<string> _log = new List<string>();

        private string _encounterId;
        private EncounterView _view;
        private bool _busy;
        private ArenaKind _arena = ArenaKind.Field;
        private int _zombieCount = 4;
        private bool _shotClock = true;
        private float _shotClockSeconds = 2f;
        private float _lastActionTime;
        private Vector2 _logScroll;

        public EncounterDebugPanel(GameClient client) => _client = client;

        private bool InFight => _view != null && _view.Status == EncounterStatus.Ongoing;

        // ------------------------------------------------------------------ input

        public void Tick()
        {
            if (!InFight || _busy) return;
            if (_shotClock && Time.unscaledTime - _lastActionTime > ShotClockLimit())
            {
                Log("(you hesitate…)");
                Send(EncounterAction.Of(EncounterActionKind.Wait));
            }
        }

        /// <summary>Panic later; for now low endurance shortens the clock.</summary>
        private float ShotClockLimit() => _view != null && _view.Endurance < Rules.ExhaustedBelow ? _shotClockSeconds * 0.6f : _shotClockSeconds;

        public void Turn(int steps)
        {
            if (InFight && !_busy) Send(EncounterAction.Turn(steps));
        }

        /// <summary>Dev: start (or restart) an encounter from elsewhere, e.g. the map's "Force encounter" buttons.</summary>
        public void Force(ArenaKind arena, int zombieCount)
        {
            if (!_client.IsConnected || _busy) return;
            _arena = arena;
            _zombieCount = Mathf.Clamp(zombieCount, 1, 12);
            StartEncounter();
        }

        /// <summary>Take over a fight the server started, e.g. when the player walked into zombies on the map.</summary>
        public void Adopt(EncounterStateMessage started)
        {
            _encounterId = started.EncounterId;
            _view = started.Result.View;
            _arena = _view.Arena;
            _log.Clear();
            Log($"You walked into {_view.ZombiesRemaining} zombie{(_view.ZombiesRemaining == 1 ? "" : "s")} ({_arena}).");
            _lastActionTime = Time.unscaledTime;
        }

        // ------------------------------------------------------------------ drawing

        public void OnGUI(Rect area)
        {
            GUILayout.BeginArea(area);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Arena", GUILayout.Width(50));
            _arena = (ArenaKind)GUILayout.Toolbar((int)_arena, Enum.GetNames(typeof(ArenaKind)), GUILayout.Width(330));
            GUILayout.Space(10);
            GUILayout.Label($"Zombies {_zombieCount}", GUILayout.Width(80));
            _zombieCount = Mathf.RoundToInt(GUILayout.HorizontalSlider(_zombieCount, 1, 12, GUILayout.Width(100)));
            GUILayout.Space(10);
            _shotClock = GUILayout.Toggle(_shotClock, $"Shot clock {_shotClockSeconds:0.0}s", GUILayout.Width(120));
            _shotClockSeconds = GUILayout.HorizontalSlider(_shotClockSeconds, 1f, 5f, GUILayout.Width(80));
            GUILayout.Space(10);
            GUI.enabled = _client.IsConnected && !_busy;
            if (GUILayout.Button(InFight ? "Restart" : "Start encounter", GUILayout.Width(120))) StartEncounter();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            if (_view == null)
            {
                GUI.Label(new Rect(area.x, area.y + 40, area.width, 60),
                    _client.IsConnected ? "Start an encounter." : "Connect to the server first (dotnet run in server/ZombieMap.Server).");
                return;
            }

            float top = area.y + 36;
            float size = Mathf.Min(area.height - 36 - 130, area.width * 0.55f);
            var radial = new Rect(area.x, top, size, size);
            var side = new Rect(radial.xMax + 12, top, area.xMax - radial.xMax - 12, size);
            var actions = new Rect(area.x, radial.yMax + 8, area.width, 120);

            // Buttons first so GUI.tooltip (hovered action) is known when drawing ghosts.
            int previewTicks = DrawActions(actions);
            DrawRadial(radial, previewTicks);
            DrawSide(side);
        }

        private int DrawActions(Rect r)
        {
            GUILayout.BeginArea(r);
            bool can = InFight && !_busy;
            GUI.enabled = can;

            var survivor = SurvivorFromView();
            GUILayout.BeginHorizontal();
            if (Button("< Turn", EncounterActionKind.Turn)) Send(EncounterAction.Turn(-1));
            if (Button("Turn >", EncounterActionKind.Turn)) Send(EncounterAction.Turn(1));
            if (Button("Turn 180", EncounterActionKind.Turn)) Send(EncounterAction.Turn(4));
            GUILayout.Space(16);
            foreach (HitZone zone in Enum.GetValues(typeof(HitZone)))
            {
                int pct = Mathf.RoundToInt((float)Rules.HitChance(zone, survivor) * 100);
                if (Button($"Hit {zone} {pct}%", EncounterActionKind.Attack)) Send(EncounterAction.Attack(zone));
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (Button("Push", EncounterActionKind.Push)) Send(EncounterAction.Of(EncounterActionKind.Push));
            if (Button("Stomp", EncounterActionKind.Stomp)) Send(EncounterAction.Of(EncounterActionKind.Stomp));
            if (Button("Wait", EncounterActionKind.Wait)) Send(EncounterAction.Of(EncounterActionKind.Wait));
            GUILayout.Space(16);
            if (Button("Patch heal", EncounterActionKind.PatchHeal)) Send(EncounterAction.Of(EncounterActionKind.PatchHeal));
            if (Button("Full heal", EncounterActionKind.FullHeal)) Send(EncounterAction.Of(EncounterActionKind.FullHeal));
            GUILayout.Space(16);
            if (Button("Flee", EncounterActionKind.Flee)) Send(EncounterAction.Of(EncounterActionKind.Flee));
            GUILayout.EndHorizontal();

            GUI.enabled = true;
            if (InFight && _shotClock)
            {
                float left = Mathf.Max(0, ShotClockLimit() - (Time.unscaledTime - _lastActionTime));
                GUILayout.Label($"Shot clock: {new string('#', Mathf.CeilToInt(left * 5))} {left:0.0}s");
            }
            else if (_view != null && !InFight)
            {
                GUILayout.Label($"ENCOUNTER OVER: {_view.Status}{(_view.DeathCause != null ? " — " + _view.DeathCause : "")}");
            }
            GUILayout.Label("Hover an action to preview where zombies will be when it finishes.  Keys: A/D or arrow keys turn.  Swipe on touch.");
            GUILayout.EndArea();

            // Tooltips are "ticks:N" for the hovered button.
            if (GUI.tooltip.StartsWith("ticks:") && int.TryParse(GUI.tooltip.Substring(6), out int t)) return t;
            return 0;

            bool Button(string label, EncounterActionKind kind)
            {
                int cost = Rules.TickCost(kind, survivor);
                string text = cost == 0 ? label : $"{label} ({cost})";
                return GUILayout.Button(new GUIContent(text, "ticks:" + cost), GUILayout.Height(34));
            }
        }

        private void DrawRadial(Rect r, int previewTicks)
        {
            GUI.Box(r, "");
            var center = r.center;
            float ringStep = r.width * 0.5f / (Rules.MaxRing + 1.3f);

            // Ring labels along the "up" (facing) direction.
            for (int ring = 0; ring <= Rules.MaxRing; ring++)
                GUI.Label(new Rect(center.x + 4, center.y - RingRadius(ring) - 8, 60, 18), ring == 0 ? "" : $"r{ring}");

            for (int lane = 0; lane < Lanes.Count; lane++)
            {
                var outer = LanePos(lane, Rules.MaxRing + 0.9f);
                bool blocked = _view.BlockedLanes.Contains(lane);
                string name = Lanes.Name(lane) + (lane == _view.Facing ? " ^" : "");
                GUI.Label(new Rect(outer.x - 30, outer.y - 10, 60, 20), blocked ? $"{Lanes.Name(lane)} X car" : name, Centered());
            }

            GUI.Box(new Rect(center.x - 24, center.y - 16, 48, 32), "YOU");

            foreach (var s in _view.Sounds)
            {
                var p = LanePos(s.Lane, s.Close ? 1.2f : 2.5f);
                GUI.Label(new Rect(p.x - 40, p.y - 10, 80, 20), s.Close ? "((GROAN))" : "(( ? ))", Centered());
            }

            foreach (var z in _view.Zombies)
            {
                var p = LanePos(z.Lane, z.Ring == 0 ? 0.55f : z.Ring);
                string label;
                if (z.Silhouette) label = z.KnockedDownTicks > 0 ? "? down" : "?";
                else
                {
                    string kind = z.Kind == ZombieKind.Runner ? "RUN" : z.Kind == ZombieKind.Crawler ? "crawl" : "Z";
                    int? contact = EncounterPreview.TicksUntilContact(z);
                    label = z.KnockedDownTicks > 0 ? $"{kind} DOWN {z.KnockedDownTicks}" : $"{kind} #{z.Id} t-{contact}";
                }
                var old = GUI.color;
                if (z.Ring == 0) GUI.color = new Color(1f, 0.4f, 0.4f);
                GUI.Box(new Rect(p.x - 42, p.y - 12, 84, 24), label);
                GUI.color = old;
            }

            if (previewTicks > 0)
            {
                var old = GUI.color;
                GUI.color = new Color(0.6f, 0.9f, 1f, 0.75f);
                foreach (var g in EncounterPreview.Predict(_view, previewTicks))
                {
                    var p = LanePos(g.Lane, g.Ring == 0 ? 0.55f : g.Ring);
                    GUI.Box(new Rect(p.x - 30, p.y + 12, 60, 18), $"ghost #{g.Id}");
                }
                GUI.color = old;
                GUI.Label(new Rect(r.x + 6, r.yMax - 22, r.width, 20), $"Preview: +{previewTicks} ticks");
            }

            float RingRadius(int ring) => (ring + 0.55f) * ringStep;

            // Facing is always drawn "up": rotate lanes by -facing.
            Vector2 LanePos(int lane, float ring)
            {
                int rel = Lanes.Rotate(lane - _view.Facing, 0);
                float angle = rel * Mathf.PI / 4f;
                float radius = (ring + 0.55f) * ringStep;
                return new Vector2(center.x + Mathf.Sin(angle) * radius, center.y - Mathf.Cos(angle) * radius);
            }
        }

        private void DrawSide(Rect r)
        {
            GUILayout.BeginArea(r);
            GUILayout.Label($"Tick {_view.Tick}   Arena {_view.Arena}   Zombies left {_view.ZombiesRemaining}");
            GUILayout.Label($"Blood {_view.Blood}/100   Endurance {_view.Endurance}/100{(_view.Endurance < Rules.ExhaustedBelow ? "  EXHAUSTED (+1 tick)" : "")}");
            var s = _view.Supplies;
            GUILayout.Label($"Rags {s.Rags}   Bandages {s.Bandages}   Disinfectant {s.Disinfectant}");
            GUILayout.Label("Wounds:");
            if (_view.Wounds.Count == 0) GUILayout.Label("  none");
            foreach (var w in _view.Wounds)
                GUILayout.Label($"  {w.Kind} — {w.Part}{(w.Bleeding ? "  BLEEDING" : "")}  [{w.Treatment}]");
            GUILayout.Space(6);
            GUILayout.Label("Log:");
            _logScroll = GUILayout.BeginScrollView(_logScroll);
            for (int i = _log.Count - 1; i >= 0; i--) GUILayout.Label(_log[i]);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static GUIStyle _centered;
        private static GUIStyle Centered() => _centered ?? (_centered = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter });

        // ------------------------------------------------------------------ network

        private async void StartEncounter()
        {
            _busy = true;
            try
            {
                var msg = await _client.Connection.RequestAsync<EncounterStateMessage>(MessageTypes.StartEncounter,
                    new StartEncounterRequest { Arena = _arena, ZombieCount = _zombieCount });
                _encounterId = msg.EncounterId;
                _view = msg.Result.View;
                _log.Clear();
                Log($"Encounter {_encounterId}: {_view.ZombiesRemaining} zombies in the {_arena}.");
                _lastActionTime = Time.unscaledTime;
            }
            catch (Exception ex)
            {
                Log("Start failed: " + ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        private async void Send(EncounterAction action)
        {
            if (_busy || _encounterId == null) return;
            _busy = true;
            try
            {
                var msg = await _client.Connection.RequestAsync<EncounterStateMessage>(MessageTypes.EncounterAct,
                    new EncounterActRequest { EncounterId = _encounterId, Action = action });
                var result = msg.Result;
                if (!result.Accepted) Log("x " + result.Error);
                foreach (var e in result.Events)
                    if (!string.IsNullOrEmpty(e.Text)) Log($"t{e.Tick}: {e.Text}");
                _view = result.View;
            }
            catch (Exception ex)
            {
                Log("Action failed: " + ex.Message);
            }
            finally
            {
                _lastActionTime = Time.unscaledTime;
                _busy = false;
            }
        }

        private void Log(string line)
        {
            _log.Add(line);
            if (_log.Count > 200) _log.RemoveAt(0);
        }

        /// <summary>Rebuild enough of the survivor from the view to show hit chances and tick costs.</summary>
        private Survivor SurvivorFromView()
        {
            var s = new Survivor();
            if (_view == null) return s;
            s.Endurance = _view.Endurance;
            s.Blood = _view.Blood;
            s.Facing = _view.Facing;
            s.Wounds = _view.Wounds.Select(w => new Wound
            {
                Part = w.Part, Kind = w.Kind, Bleeding = w.Bleeding, Bandage = w.Bandage, Disinfected = w.Disinfected,
            }).ToList();
            return s;
        }
    }
}
