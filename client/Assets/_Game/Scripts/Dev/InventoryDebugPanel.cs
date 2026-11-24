using UnityEngine;
using ZombieMap.Core.Items;

namespace ZombieMap.Client.Dev
{
    /// <summary>
    /// Inventory tab: pockets and the worn bag, weights, and the moves the server allows
    /// (move between pockets and bag, wear/take off a bag, drop, put into the open container).
    /// </summary>
    public sealed class InventoryDebugPanel
    {
        private readonly LootSession _loot;
        private Vector2 _scroll;

        public InventoryDebugPanel(LootSession loot) => _loot = loot;

        public void OnGUI(Rect area)
        {
            GUILayout.BeginArea(area);
            var inv = _loot.Inventory;
            if (inv == null)
            {
                GUILayout.Label("No inventory yet. Connect to the server.");
                if (GUILayout.Button("Refresh", GUILayout.Width(90))) _ = _loot.RefreshInventory();
                GUILayout.EndArea();
                return;
            }

            GUILayout.BeginHorizontal();
            var weightStyle = inv.HeavyLoad ? Warn : GUI.skin.label;
            GUILayout.Label($"Carrying {inv.CarriedWeight:0.0} kg of {InventoryRules.CarryLimit:0} kg comfortable" +
                            (inv.HeavyLoad ? "  HEAVY LOAD" : ""), weightStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", GUILayout.Width(80))) _ = _loot.RefreshInventory();
            GUILayout.EndHorizontal();

            var open = _loot.OpenContainer;
            GUILayout.Label(open != null
                ? $"Open container: {open.Name} ({InventoryRules.Free(open):0.0} kg free). \"Put\" puts things in it."
                : "Search a container in the Building tab to be able to put things into it.");
            if (!string.IsNullOrEmpty(_loot.Message)) GUILayout.Label(_loot.Message, Warn);

            _scroll = GUILayout.BeginScrollView(_scroll);
            GUI.enabled = !_loot.Busy;

            DrawContainer(inv.Main, inBag: false, inv);
            GUILayout.Space(10);
            if (inv.Back != null)
            {
                GUILayout.BeginHorizontal();
                var def = inv.Back.Def();
                GUILayout.Label($"<b>On your back: {def.Name}</b>  (contents {def.WeightReduction:P0} lighter)", Rich);
                if (GUILayout.Button("Take off", GUILayout.Width(80))) _ = _loot.Unequip();
                GUILayout.EndHorizontal();
                DrawContainer(inv.Back.Contents, inBag: true, inv);
            }
            else
            {
                GUILayout.Label("No bag on your back. Pick up a backpack and press Wear.");
            }

            GUI.enabled = true;
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawContainer(ItemContainer c, bool inBag, PlayerInventory inv)
        {
            GUILayout.Label($"<b>{c.Name}</b>  {InventoryRules.Used(c):0.0} / {c.Capacity:0} kg", Rich);
            if (c.Items.Count == 0) GUILayout.Label("   (empty)");
            foreach (var s in c.Items)
            {
                var def = s.Def();
                GUILayout.BeginHorizontal();
                GUILayout.Label($"   {Describe(s, def)}", GUILayout.Width(330));
                GUILayout.Label($"{InventoryRules.Weight(s):0.00} kg", GUILayout.Width(60));

                if (!inBag && inv.Back != null && GUILayout.Button("To bag", GUILayout.Width(65))) _ = _loot.Move(s.Id, PlayerInventory.BackId);
                if (inBag && GUILayout.Button("To pockets", GUILayout.Width(80))) _ = _loot.Move(s.Id, PlayerInventory.MainId);
                if (!inBag && def.IsBag && def.WornOnBack && GUILayout.Button("Wear", GUILayout.Width(55))) _ = _loot.Equip(s.Id);
                if (_loot.OpenContainer != null && GUILayout.Button("Put", GUILayout.Width(45))) _ = _loot.Put(s.Id);
                if (GUILayout.Button("Drop", GUILayout.Width(50))) _ = _loot.Drop(s.Id);
                GUILayout.EndHorizontal();
            }
        }

        public static string Describe(ItemStack s, ItemDefinition def)
        {
            string text = s.Count > 1 ? $"{s.Count}× {def.Name}" : def.Name;
            if (def.Uses > 0) text += $" ({s.UsesLeft}/{def.Uses} uses)";
            if (def.Category == ItemCategory.Weapon || def.Category == ItemCategory.Clothing) text += $" ({s.Condition:P0})";
            if (s.FreshUntilDay > 0)
            {
                long today = LootGenerator.UnixDay(System.DateTime.UtcNow);
                long days = s.FreshUntilDay - today;
                text += days < 0 ? " (rotten)" : days == 0 ? " (goes off today)" : $" (fresh {days} d)";
            }
            if (def.IsBag && s.Contents != null && s.Contents.Items.Count > 0) text += $" [{s.Contents.Items.Count} inside]";
            return text;
        }

        private static GUIStyle _rich, _warn;
        private static GUIStyle Rich => _rich ?? (_rich = new GUIStyle(GUI.skin.label) { richText = true });
        private static GUIStyle Warn => _warn ?? (_warn = new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(1f, 0.6f, 0.3f) } });
    }
}
