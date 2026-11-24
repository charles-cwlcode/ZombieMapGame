using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ZombieMap.Client.Health;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Health;
using ZombieMap.Core.Items;

namespace ZombieMap.Client.Dev
{
    /// <summary>
    /// Health tab: the 3D body (<see cref="HealthScreen"/>) with a callout for every injured zone, a
    /// line from the zone to a box listing its wounds, their state, healing % and treatment buttons.
    /// Tap any zone to see what clothing protects it. Blood, endurance and worn clothes along the edges.
    /// </summary>
    public sealed class HealthDebugPanel
    {
        private const float BoxWidth = 290f;
        private const float Gap = 8f;

        private readonly PlayerSession _session;
        private HealthScreen _screen;
        private BodyPart? _selected;
        private readonly List<Rect> _boxes = new List<Rect>();

        public HealthDebugPanel(PlayerSession session)
        {
            _session = session;
            _session.BodyChanged += () => _screen?.SetBody(_session.Body);
        }

        public void SetVisible(bool visible)
        {
            if (visible && _screen == null)
            {
                _screen = HealthScreen.Create();
                _screen?.SetBody(_session.Body);
            }
            _screen?.Show(visible);
        }

        public void OnGUI(Rect area, float scale)
        {
            var body = _session.Body;
            var inv = _session.Inventory;
            if (_screen == null || body == null || inv == null)
            {
                GUI.Label(new Rect(area.x, area.y, 400, 40), _screen == null ? "Body model missing (Resources/Health/Survivor)." : "Connect to see your body.");
                return;
            }

            _boxes.Clear();
            _screen.Selected = _selected; // thicker stroke on the 3D overlay
            DrawStatus(new Rect(area.x, area.y, area.width, 46), body, inv);
            if (body.Dead)
            {
                var r = new Rect(area.center.x - 160, area.y + 60, 320, 70);
                GUI.Box(r, "");
                GUI.Label(new Rect(r.x + 10, r.y + 6, 300, 24), $"<b>Survivor #{_session.SurvivorNumber} died: {body.DeathCause}.</b>", Rich);
                string keep = _session.Safehouse != null ? "safehouse stash is kept" : "everything carried is lost";
                if (GUI.Button(new Rect(r.x + 10, r.y + 36, 300, 26), $"Next survivor ({keep})")) _ = _session.NewSurvivor();
                return;
            }

            var callouts = new List<(BodyPart zone, Vector2 anchor, List<Row> rows)>();
            foreach (var zone in BodyZoneModel.Zones)
            {
                var wounds = body.Wounds.Where(w => w.Part == zone).ToList();
                if (wounds.Count == 0 && _selected != zone) continue;
                if (!_screen.Body.HasZone(zone)) continue;
                var p = _screen.ScreenAnchor(zone);
                callouts.Add((zone, new Vector2(p.x / scale, (Screen.height - p.y) / scale), Rows(zone, wounds, inv)));
            }

            float centre = area.center.x;
            float top = area.y + 54, bottom = area.yMax - 150;
            foreach (bool left in new[] { true, false })
            {
                float y = top;
                foreach (var c in callouts.Where(c => (c.anchor.x < centre) == left).OrderBy(c => c.anchor.y))
                {
                    float h = BoxHeight(c.rows);
                    float want = Mathf.Clamp(c.anchor.y - h / 2, top, Mathf.Max(top, bottom - h));
                    y = Mathf.Max(y, want);
                    var box = new Rect(left ? area.x : area.xMax - BoxWidth, y, BoxWidth, h);
                    var colour = _screen.LineColor(c.zone);
                    colour.a = _screen.FacesCamera(c.zone) ? 1f : 0.35f;
                    var edge = new Vector2(left ? box.xMax : box.x, box.y + 12);
                    DrawLine(edge, c.anchor, colour, 2f);
                    DrawDot(c.anchor, colour);
                    DrawBox(box, c.rows);
                    _boxes.Add(box);
                    y = box.yMax + Gap;
                }
            }

            DrawClothing(new Rect(area.x, area.yMax - 140, 360, 140), inv);
            DrawLegend(new Rect(area.xMax - 340, area.yMax - 108, 340, 108));
            GUI.Label(new Rect(area.center.x - 150, area.yMax - 22, 300, 20), "Drag or Q/E to turn. Tap a body part for details.", Centered);

            // Tap the body to inspect a zone (clicks on boxes and buttons are theirs).
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && !_boxes.Any(b => b.Contains(e.mousePosition)) &&
                e.mousePosition.y > area.y + 50 && e.mousePosition.y < area.yMax - 150)
            {
                var pixel = e.mousePosition * scale;
                if (_screen.TryPick(new Vector2(pixel.x, Screen.height - pixel.y), out var zone)) _selected = zone == _selected ? (BodyPart?)null : zone;
            }
        }

        // ------------------------------------------------------------------ parts

        private void DrawStatus(Rect r, BodyView body, PlayerInventory inv)
        {
            Bar(new Rect(r.x, r.y + 4, 220, 16), body.Blood / 100f, new Color(0.75f, 0.1f, 0.1f), $"Blood {body.Blood}%");
            Bar(new Rect(r.x, r.y + 24, 220, 16), body.Endurance / 100f, new Color(0.25f, 0.55f, 0.85f),
                $"Endurance {body.Endurance}%" + (body.EnduranceCap < 100 ? $" (max {body.EnduranceCap}: tired)" : ""));
            DrawNeeds(new Rect(r.x + 570, r.y, 360, r.height), body);

            float x = r.x + 235;
            int bleeding = body.Wounds.Count(w => w.Bleeding);
            string summary = body.Wounds.Count == 0 ? "No injuries." :
                $"{body.Wounds.Count} wound{(body.Wounds.Count == 1 ? "" : "s")}" + (bleeding > 0 ? $", <color=#ff5040>{bleeding} bleeding</color>" : "") +
                (body.Wounds.Any(w => w.Infected) ? ", <color=#90d040>infected</color>" : "");
            GUI.Label(new Rect(x, r.y + 2, 330, 20), summary, Rich);

            long left = body.AntibioticsUntil - _session.BodyTime;
            var pills = Find(inv, d => d.Medical == MedicalUse.Antibiotics);
            if (left > 0) GUI.Label(new Rect(x, r.y + 22, 330, 20), $"On antibiotics ({left / 3600f:0.0} h left)");
            else if (pills != null && GUI.Button(new Rect(x, r.y + 22, 150, 20), $"Take antibiotics ({pills.UsesLeft})")) _ = _session.TakeMedicine(pills.Id);

            GUI.enabled = !_session.Busy;
            if (GUI.Button(new Rect(r.xMax - 80, r.y + 4, 80, 22), "Refresh")) _ = _session.RefreshHealth();
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(_session.Message)) GUI.Label(new Rect(r.xMax - 420, r.y + 26, 420, 20), _session.Message, Warn);
        }

        /// <summary>Food, water and rest bars, and sleeping (only at your safehouse).</summary>
        private void DrawNeeds(Rect r, BodyView body)
        {
            Bar(new Rect(r.x, r.y + 4, 170, 16), body.Food / 100f, new Color(0.75f, 0.55f, 0.2f), body.Food <= 0 ? "Starving" : $"Food {body.Food}%");
            Bar(new Rect(r.x, r.y + 24, 170, 16), body.Water / 100f, new Color(0.2f, 0.6f, 0.75f), body.Water <= 0 ? "Dehydrated" : $"Water {body.Water}%");
            int rest = 100 - body.Fatigue;
            Bar(new Rect(r.x + 180, r.y + 4, 170, 16), rest / 100f, new Color(0.5f, 0.45f, 0.8f), body.Asleep ? $"Asleep, rest {rest}%" : $"Rest {rest}%");

            GUI.enabled = !_session.Busy;
            var button = new Rect(r.x + 180, r.y + 24, 170, 18);
            if (body.Asleep)
            {
                if (GUI.Button(button, "Wake up")) _ = _session.Wake();
            }
            else if (_session.Safehouse == null) GUI.Label(button, "No safehouse to sleep in.", Small);
            else if (GUI.Button(button, "Sleep (at your safehouse)")) _ = _session.Sleep();
            GUI.enabled = true;
        }

        /// <summary>One line in a callout box: text, an optional healing bar, optional buttons.</summary>
        private sealed class Row
        {
            public string Text;
            public float? Bar;
            public List<(string label, System.Action action)> Buttons;
        }

        private const float Pad = 6f, ButtonHeight = 22f, BarHeight = 8f;

        private List<Row> Rows(BodyPart zone, List<WoundView> wounds, PlayerInventory inv)
        {
            var rows = new List<Row> { new Row { Text = $"<b>{Title(zone)}</b>   {StatusText(_screen.Status(zone))}" } };
            if (wounds.Count == 0)
            {
                var cover = inv.Worn.Where(w => w.Def().Covers != null && w.Def().Covers.Contains(zone)).ToList();
                rows.Add(new Row
                {
                    Text = cover.Count == 0 ? "No clothing covers it." :
                        "Covered by " + string.Join(", ", cover.Select(c =>
                            $"{c.Def().Name} {c.Condition:P0} (stops {c.Def().BiteDefense * c.Condition:P0} of bites)")),
                });
                return rows;
            }

            int rags = inv.CountOf(d => d.Medical == MedicalUse.Rag);
            int bandages = inv.CountOf(d => d.Medical == MedicalUse.Bandage);
            int disinfect = Units(inv, d => d.Medical == MedicalUse.Disinfectant);
            int needles = inv.CountOf(d => d.Medical == MedicalUse.SutureNeedle);
            foreach (var w in wounds)
            {
                var buttons = new List<(string, System.Action)>();
                int id = w.Id;
                if (w.Bandage == BandageKind.None || w.BandageDirty)
                {
                    if (rags > 0) buttons.Add(($"Rag ({rags})", () => _ = _session.Treat(id, TreatAction.BandageWithRag)));
                    if (bandages > 0) buttons.Add(($"Bandage ({bandages})", () => _ = _session.Treat(id, TreatAction.BandageWithBandage)));
                }
                if ((!w.Disinfected || w.Infected) && disinfect > 0) buttons.Add(($"Disinfect ({disinfect})", () => _ = _session.Treat(id, TreatAction.Disinfect)));
                if (w.Kind == WoundKind.Laceration && !w.Stitched && needles > 0) buttons.Add(("Stitch", () => _ = _session.Treat(id, TreatAction.Stitch)));
                if (w.Bandage != BandageKind.None) buttons.Add(("Remove", () => _ = _session.Treat(id, TreatAction.RemoveBandage)));
                rows.Add(new Row { Text = $"{w.Kind}: {State(w)}", Bar = (float)w.Healed, Buttons = buttons });
                if (buttons.Count == 0 && HealthScreen.NeedsAttention(w))
                    rows.Add(new Row { Text = "<color=#ff9070>You have nothing to treat this with.</color>" });
            }
            return rows;
        }

        private static float RowHeight(Row r, float width)
        {
            float h = Rich.CalcHeight(new GUIContent(r.Text), width);
            if (r.Bar.HasValue) h += BarHeight + 3;
            if (r.Buttons != null && r.Buttons.Count > 0) h += ButtonRows(r.Buttons, width) * (ButtonHeight + 2) + 2;
            return h + 3;
        }

        /// <summary>Buttons wrap onto another line if they don't fit.</summary>
        private static int ButtonRows(List<(string label, System.Action action)> buttons, float width)
        {
            int rows = 1;
            float x = 0;
            foreach (var (label, _) in buttons)
            {
                float w = GUI.skin.button.CalcSize(new GUIContent(label)).x + 4;
                if (x > 0 && x + w > width) { rows++; x = 0; }
                x += w;
            }
            return rows;
        }

        private static float BoxHeight(List<Row> rows) => Pad * 2 + rows.Sum(r => RowHeight(r, BoxWidth - Pad * 2));

        private void DrawBox(Rect box, List<Row> rows)
        {
            GUI.Box(box, "");
            GUI.Box(box, ""); // twice: the default skin's box is too see-through over the body
            float width = box.width - Pad * 2;
            float y = box.y + Pad;
            foreach (var r in rows)
            {
                float th = Rich.CalcHeight(new GUIContent(r.Text), width);
                GUI.Label(new Rect(box.x + Pad, y, width, th), r.Text, Rich);
                y += th;
                if (r.Bar.HasValue)
                {
                    Bar(new Rect(box.x + Pad, y + 1, width, BarHeight), r.Bar.Value, HealthScreen.Green, "");
                    y += BarHeight + 3;
                }
                if (r.Buttons != null && r.Buttons.Count > 0)
                {
                    float x = 0;
                    y += 2;
                    GUI.enabled = !_session.Busy;
                    foreach (var (label, action) in r.Buttons)
                    {
                        float w = GUI.skin.button.CalcSize(new GUIContent(label)).x + 4;
                        if (x > 0 && x + w > width) { x = 0; y += ButtonHeight + 2; }
                        if (GUI.Button(new Rect(box.x + Pad + x, y, w - 2, ButtonHeight), label)) action();
                        x += w;
                    }
                    GUI.enabled = true;
                    y += ButtonHeight + 2;
                }
                y += 3;
            }
        }

        private static string StatusText(ZoneStatus s)
        {
            switch (s)
            {
                case ZoneStatus.Bitten: return "<color=#c8c8cc>■ BITTEN</color>";
                case ZoneStatus.NeedsAttention: return "<color=#ff5a46>■ Needs attention</color>";
                case ZoneStatus.Treated: return "<color=#f5a91f>■ Treated</color>";
                default: return "<color=#4fbf60>■ OK</color>";
            }
        }

        private static void DrawLegend(Rect r)
        {
            GUILayout.BeginArea(r, GUI.skin.box);
            GUILayout.Label(StatusText(ZoneStatus.Ok) + "  nothing wrong", Rich);
            GUILayout.Label(StatusText(ZoneStatus.Treated) + "  bandaged, healing", Rich);
            GUILayout.Label(StatusText(ZoneStatus.NeedsAttention) + "  bleeding, open, infected, dirty bandage", Rich);
            GUILayout.Label(StatusText(ZoneStatus.Bitten) + "  bitten: the virus is likely", Rich);
            GUILayout.EndArea();
        }

        private void DrawClothing(Rect r, PlayerInventory inv)
        {
            GUILayout.BeginArea(r, GUI.skin.box);
            GUILayout.Label("<b>Wearing</b>", Rich);
            if (inv.Worn.Count == 0) GUILayout.Label("Nothing. Bites go straight in.");
            foreach (var c in inv.Worn.OrderBy(c => c.Def().Slot))
            {
                var d = c.Def();
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{d.Name} ({c.Condition:P0}): bite {d.BiteDefense * c.Condition:P0}, scratch {d.ScratchDefense * c.Condition:P0}", GUILayout.Width(280));
                GUI.enabled = !_session.Busy;
                if (GUILayout.Button("Off", GUILayout.Width(40))) _ = _session.TakeOff(c.Id);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndArea();
        }

        // ------------------------------------------------------------------ text

        private static string State(WoundView w)
        {
            var parts = new List<string>();
            if (w.Bleeding) parts.Add("<color=#ff5040>Bleeding</color>");
            if (w.Infected) parts.Add("<color=#90d040>Infected</color>");
            if (w.Bandage == BandageKind.None) { if (!w.Bleeding) parts.Add("<color=#e08060>Open</color>"); }
            else if (w.BandageDirty) parts.Add($"<color=#c09060>Dirty {Lower(w.Bandage)}</color>");
            else parts.Add($"<color=#80d080>{(w.Bandage == BandageKind.Rag ? "Rag bandage" : "Bandaged")}</color>");
            if (w.Disinfected && !w.Infected) parts.Add("<color=#80d080>Disinfected</color>");
            if (w.Stitched) parts.Add("<color=#80d080>Stitched</color>");
            parts.Add($"healing {w.Healed:P0}");
            return string.Join(", ", parts);
        }

        private static string Lower(BandageKind k) => k == BandageKind.Rag ? "rag" : "bandage";

        private static string Title(BodyPart zone)
        {
            var s = BodyParts.Pretty(zone);
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private static ItemStack Find(PlayerInventory inv, System.Func<ItemDefinition, bool> match) =>
            new[] { inv.Main, inv.Back?.Contents }.Where(c => c != null).SelectMany(c => c.Items).FirstOrDefault(s => match(s.Def()));

        private static int Units(PlayerInventory inv, System.Func<ItemDefinition, bool> match) =>
            new[] { inv.Main, inv.Back?.Contents }.Where(c => c != null).SelectMany(c => c.Items)
                .Where(s => match(s.Def())).Sum(s => InventoryRules.Units(s));

        // ------------------------------------------------------------------ drawing

        private static void Bar(Rect r, float fill, Color colour, string label)
        {
            var old = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.6f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = colour;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(fill), r.height), Texture2D.whiteTexture);
            GUI.color = old;
            if (label.Length > 0) GUI.Label(new Rect(r.x + 4, r.y - 2, r.width, r.height + 4), label, Small);
        }

        private static void DrawLine(Vector2 a, Vector2 b, Color colour, float width)
        {
            var matrix = GUI.matrix;
            var old = GUI.color;
            GUI.color = colour;
            float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
            GUIUtility.RotateAroundPivot(angle, a);
            GUI.DrawTexture(new Rect(a.x, a.y - width / 2, (b - a).magnitude, width), Texture2D.whiteTexture);
            GUI.matrix = matrix;
            GUI.color = old;
        }

        private static void DrawDot(Vector2 p, Color colour)
        {
            var old = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(new Rect(p.x - 4, p.y - 4, 8, 8), Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static GUIStyle _rich, _warn, _small, _centered;
        private static GUIStyle Rich => _rich ?? (_rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });
        private static GUIStyle Warn => _warn ?? (_warn = new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(1f, 0.6f, 0.3f) } });
        private static GUIStyle Small => _small ?? (_small = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = Color.white } });
        private static GUIStyle Centered => _centered ?? (_centered = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter });
    }
}
