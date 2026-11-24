using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ZombieMap.Client.Map;
using ZombieMap.Client.Net;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Protocol;
using Object = UnityEngine.Object;

namespace ZombieMap.Client.Editor
{
    /// <summary>
    /// End-to-end check of the map in Play mode (needs graphics, so not -batchmode):
    ///   Unity -projectPath client -executeMethod ZombieMap.Client.Editor.MapPlayModeTest.Run [-screenshot path.png]
    /// Opens the dev scene, waits for the server connection, simulated GPS, map and tiles, taps outward
    /// from the start point until it hits a real building, fetches its floor plan, takes a
    /// screenshot and exits (0 = passed).
    /// </summary>
    [InitializeOnLoad]
    public static class MapPlayModeTest
    {
        private const string RunningKey = "zm.mapTest.running";
        private const string ShotKey = "zm.mapTest.screenshot";
        private const double TimeoutSeconds = 150;

        private static double _startTime;
        private static bool _busy;
        private static string _step = "";

        static MapPlayModeTest()
        {
            if (SessionState.GetBool(RunningKey, false)) EditorApplication.update += Tick;
        }

        [MenuItem("ZombieMap/Run Map Play-Mode Test")]
        public static void Run()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-screenshot");
            SessionState.SetString(ShotKey, i >= 0 && i + 1 < args.Length ? args[i + 1] : "Temp/map-test.png");
            SessionState.SetBool(RunningKey, true);
            EditorApplication.update -= Tick; // in case domain reload on Play is turned off
            EditorApplication.update += Tick;
            EditorSceneManager.OpenScene(DevTools.DevScenePath);
            EditorApplication.EnterPlaymode();
        }

        private static void Tick()
        {
            if (!EditorApplication.isPlaying || _busy) return;
            if (_startTime == 0) _startTime = EditorApplication.timeSinceStartup;
            if (EditorApplication.timeSinceStartup - _startTime > TimeoutSeconds)
            {
                Finish(false, "timed out while: " + _step);
                return;
            }

            var view = Object.FindAnyObjectByType<MapView>();
            var client = Object.FindAnyObjectByType<GameClient>();
            if (view == null || client == null) { _step = "finding scene objects"; return; }
            if (!client.IsConnected) { _step = "connecting: " + client.Status; return; }
            if (!view.Location.HasFix) { _step = "waiting for GPS start point"; return; }
            if (!view.IsMapReady) { _step = "waiting for map"; return; }
            if (view.LoadedTileCount < 4 || EditorApplication.timeSinceStartup - _startTime < 20) { _step = $"loading tiles ({view.LoadedTileCount})"; return; }

            _busy = true;
            _ = RunChecks(view, client);
        }

        private static async Task RunChecks(MapView view, GameClient client)
        {
            try
            {
                Debug.Log($"[MapTest] Map ready at {view.Location.Position}, {view.LoadedTileCount} tiles, {view.MetresPerUnit:0.000} m per unit");
                BuildingInfo found = null;
                var start = view.Location.Position;
                for (int ring = 0; ring <= 12 && found == null; ring++)
                for (int dx = -ring; dx <= ring && found == null; dx++)
                for (int dy = -ring; dy <= ring && found == null; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring) continue;
                    found = await view.SelectBuildingAtAsync(GeoMath.Offset(start, dx * 12, dy * 12));
                }
                if (found == null) { Finish(false, "no building found near the start point"); return; }
                Debug.Log($"[MapTest] Selected {found.Kind} '{found.Name}' {found.BuildingId}, {found.DistanceMetres} m away, in range {found.InRange}");

                var plan = (await client.Connection.RequestAsync<FloorPlanMessage>(MessageTypes.GetFloorPlan,
                    new GetFloorPlanRequest { BuildingId = found.BuildingId })).Plan;
                Debug.Log($"[MapTest] Floor plan: {plan.Rooms.Count} rooms, {plan.Width:0}×{plan.Height:0} m");

                string shot = SessionState.GetString(ShotKey, "Temp/map-test.png");
                ScreenCapture.CaptureScreenshot(shot);
                await Task.Delay(3000); // screenshot is written at the end of a later frame

                bool looted = await LootChecks(plan, found, shot);
                Finish(plan.Rooms.Count > 0 && looted, "screenshots " + shot);
            }
            catch (Exception ex)
            {
                Finish(false, ex.ToString());
            }
        }

        /// <summary>Through the HUD: open the building, search the room with most containers, take an item.</summary>
        private static async Task<bool> LootChecks(FloorPlan plan, BuildingInfo building, string shot)
        {
            var hud = Object.FindAnyObjectByType<Dev.DevHud>();
            var room = plan.Rooms.OrderByDescending(r => r.Containers.Count).First();
            hud.ShowBuilding(plan, building.Kind.ToString(), room);
            foreach (var c in room.Containers) await hud.Loot.Search(c.Id);
            Debug.Log($"[MapTest] Searching {room.Containers.Count} containers in the {room.Type}… {hud.Loot.Message}");

            for (int i = 0; i < 60 && room.Containers.Any(c => hud.Loot.Container(c.Id)?.Contents == null); i++)
                await Task.Delay(200); // DevHud.Update → LootSession.Tick finishes each search when its time is up
            var searched = room.Containers.Select(c => hud.Loot.Container(c.Id)?.Contents).ToList();
            if (searched.Any(c => c == null)) { Debug.LogError("[MapTest] Searches didn't finish: " + hud.Loot.Message); return false; }
            Debug.Log("[MapTest] Found: " + string.Join("; ", searched.Select(c => $"{c.Name}: {c.Items.Count} items")));

            var source = room.Containers.FirstOrDefault(c => hud.Loot.Container(c.Id).Contents.Items.Count > 0);
            if (source == null) { Debug.Log("[MapTest] Room was empty; nothing to take (not a failure)."); return true; }
            var item = hud.Loot.Container(source.Id).Contents.Items[0];
            int before = hud.Loot.Inventory.Main.Items.Count;
            await hud.Loot.Take(source.Id, item.Id);
            bool took = hud.Loot.Inventory != null && hud.Loot.Inventory.Main.Items.Any(s => s.ItemId == item.ItemId && s.Id.StartsWith("i")) &&
                        hud.Loot.Container(source.Id).Contents.Find(item.Id) == null;
            Debug.Log($"[MapTest] Took {item.ItemId}: {took} (pockets {before} → {hud.Loot.Inventory?.Main.Items.Count} stacks) {hud.Loot.Message}");

            await Task.Delay(500);
            ScreenCapture.CaptureScreenshot(shot.Replace(".png", "-building.png"));
            await Task.Delay(2000);
            hud.ShowInventory();
            await Task.Delay(500);
            ScreenCapture.CaptureScreenshot(shot.Replace(".png", "-inventory.png"));
            await Task.Delay(2000);
            return took;
        }

        private static void Finish(bool ok, string detail)
        {
            EditorApplication.update -= Tick;
            SessionState.SetBool(RunningKey, false);
            Debug.Log((ok ? "[MapTest] MAP TEST PASSED: " : "[MapTest] MAP TEST FAILED: ") + detail);
            EditorApplication.ExitPlaymode();
            if (!Application.isBatchMode && Environment.GetCommandLineArgs().Length > 0 &&
                Array.IndexOf(Environment.GetCommandLineArgs(), "-executeMethod") >= 0)
                EditorApplication.delayCall += () => EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
