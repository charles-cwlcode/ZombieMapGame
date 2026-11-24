using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ZombieMap.Client.Map;
using ZombieMap.Client.Net;
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
                Finish(plan.Rooms.Count > 0, "screenshot " + shot);
            }
            catch (Exception ex)
            {
                Finish(false, ex.ToString());
            }
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
