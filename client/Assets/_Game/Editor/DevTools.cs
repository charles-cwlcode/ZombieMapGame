using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ZombieMap.Client.Dev;
using ZombieMap.Client.Net;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Client.Editor
{
    /// <summary>
    /// Editor helpers. Both can run from the command line:
    ///   Unity -batchmode -projectPath client -executeMethod ZombieMap.Client.Editor.DevTools.CreateDevScene -quit
    ///   Unity -batchmode -projectPath client -executeMethod ZombieMap.Client.Editor.DevTools.SmokeTestServer [-serverUrl ws://...]
    /// </summary>
    public static class DevTools
    {
        public const string DevScenePath = "Assets/_Game/Scenes/Dev.unity";

        [MenuItem("ZombieMap/Create Dev Scene")]
        public static void CreateDevScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.08f, 0.09f, 0.1f);
            }

            var game = new GameObject("Game");
            game.AddComponent<GameClient>();
            game.AddComponent<DevHud>();

            EditorSceneManager.SaveScene(scene, DevScenePath);

            var scenes = EditorBuildSettings.scenes.Where(s => s.path != DevScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(DevScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log($"[DevTools] Created {DevScenePath} and put it first in Build Settings.");
        }

        [MenuItem("ZombieMap/Smoke Test Server")]
        public static void SmokeTestServer()
        {
            string url = ArgValue("-serverUrl") ?? "ws://localhost:5080/ws";
            bool ok;
            try
            {
                // Batch mode has no player loop to resume awaits on, so run it on the thread pool.
                ok = Task.Run(() => RunSmokeTest(url)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.LogError("[SmokeTest] FAILED: " + ex);
                ok = false;
            }

            Debug.Log(ok ? "[SmokeTest] UNITY SMOKE TEST PASSED" : "[SmokeTest] UNITY SMOKE TEST FAILED");
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        private static async Task<bool> RunSmokeTest(string url)
        {
            using (var conn = new GameConnection())
            {
                var welcome = await conn.ConnectAsync(url, "unity-smoke-" + Guid.NewGuid().ToString("N").Substring(0, 6), "Unity Smoke Test").ConfigureAwait(false);
                Debug.Log($"[SmokeTest] Connected as {welcome.PlayerId}");

                var plan = (await conn.RequestAsync<FloorPlanMessage>(MessageTypes.GetFloorPlan, new GetFloorPlanRequest
                {
                    Footprint = new BuildingFootprint
                    {
                        BuildingId = "unity-smoke-house",
                        Kind = BuildingKind.Pharmacy,
                        Points = { new Vec2(0, 0), new Vec2(18, 0), new Vec2(18, 12), new Vec2(0, 12) },
                    },
                }).ConfigureAwait(false)).Plan;
                Debug.Log($"[SmokeTest] Floor plan: {plan.Rooms.Count} rooms: {string.Join(", ", plan.Rooms.Select(r => r.Type))}");

                var state = await conn.RequestAsync<EncounterStateMessage>(MessageTypes.StartEncounter,
                    new StartEncounterRequest { Arena = ArenaKind.CarPark, ZombieCount = 3 }).ConfigureAwait(false);
                Debug.Log($"[SmokeTest] Encounter {state.EncounterId}: blocked lanes {string.Join(",", state.Result.View.BlockedLanes.Select(Lanes.Name))}");

                var next = await conn.RequestAsync<EncounterStateMessage>(MessageTypes.EncounterAct, new EncounterActRequest
                {
                    EncounterId = state.EncounterId,
                    Action = EncounterAction.Of(EncounterActionKind.Wait),
                }).ConfigureAwait(false);
                Debug.Log($"[SmokeTest] After Wait: tick {next.Result.View.Tick}, accepted {next.Result.Accepted}");

                return plan.Rooms.Count > 0 && next.Result.Accepted && next.Result.View.Tick == 1;
            }
        }

        private static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
