using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ZombieMap.Client.Dev;
using ZombieMap.Client.Map;
using ZombieMap.Client.Net;
using Object = UnityEngine.Object;
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

        private const string MapboxRoot = "Packages/com.mapbox.sdk/Runtime/Mapbox/";
        private const string TileMaterialPath = MapboxRoot + "BaseModule/Unity/Visuals/Materials/ElevatedTerrainMaterial.mat";
        private const string BuildingLayerPath = MapboxRoot + "VectorModule/BasicVisualizers/Buildings/DefaultBuildingLayer.asset";
        private const string PlayerMaterialPath = "Assets/_Game/Materials/PlayerMarker.mat";

        [MenuItem("ZombieMap/Create Dev Scene")]
        public static void CreateDevScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var cam = Camera.main;
            cam.name = "MapCamera";
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.68f, 0.72f);
            var light = Object.FindAnyObjectByType<Light>();
            if (light != null) light.transform.rotation = Quaternion.Euler(55f, -35f, 0f);

            // Player marker. It also drives which map tiles load (TransformTileProvider).
            var player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            player.name = "Player";
            Object.DestroyImmediate(player.GetComponent<Collider>());
            player.transform.localScale = Vector3.one * 8f; // ~5 m tall: visible from the follow camera
            player.GetComponent<MeshRenderer>().sharedMaterial = PlayerMaterial();
            var tileProvider = player.AddComponent<TransformTileProvider>();
            tileProvider.TileProvider.Transform = player.transform;

            // Mapbox map: street imagery + 3D buildings. Started by MapView once we know where the player is.
            var mapGo = new GameObject("Map");
            var map = mapGo.AddComponent<Mapbox.Example.Scripts.Map.MapboxMapBehaviour>();
            mapGo.AddComponent<Mapbox.Example.Scripts.ModuleBehaviours.StaticApiLayerModuleScript>();
            var vector = mapGo.AddComponent<Mapbox.VectorModule.Unity.VectorLayerModuleScript>();
            var mapSo = new SerializedObject(map);
            mapSo.FindProperty("InitializeOnStart").boolValue = false;
            mapSo.FindProperty("_defaultTileMaterial").objectReferenceValue = Require<Material>(TileMaterialPath);
            mapSo.FindProperty("TileProvider").objectReferenceValue = tileProvider;
            mapSo.FindProperty("UnityContext.MapRoot").objectReferenceValue = mapGo.transform;
            mapSo.FindProperty("MapInformation._zoom").floatValue = 16f;
            mapSo.FindProperty("MapInformation._scale").floatValue = 1f;
            mapSo.FindProperty("MapInformation._pitch").floatValue = 50f;
            // Never commit a real location here: MapView sets the start point at runtime.
            mapSo.FindProperty("MapInformation._latitudeLongitudeString").stringValue = "0,0";
            mapSo.ApplyModifiedPropertiesWithoutUndo();
            var vectorSo = new SerializedObject(vector);
            var layers = vectorSo.FindProperty("_layerVisualizers");
            layers.arraySize = 1;
            layers.GetArrayElementAtIndex(0).objectReferenceValue = Require<ScriptableObject>(BuildingLayerPath);
            vectorSo.ApplyModifiedPropertiesWithoutUndo();

            var highlightGo = new GameObject("BuildingHighlight");
            highlightGo.AddComponent<LineRenderer>();
            var highlight = highlightGo.AddComponent<BuildingHighlight>();

            var game = new GameObject("Game");
            var client = game.AddComponent<GameClient>();
            var location = game.AddComponent<PlayerLocation>();
            location.HeadingReference = cam.transform;
            var view = game.AddComponent<MapView>();
            var viewSo = new SerializedObject(view);
            viewSo.FindProperty("map").objectReferenceValue = map;
            viewSo.FindProperty("location").objectReferenceValue = location;
            viewSo.FindProperty("client").objectReferenceValue = client;
            viewSo.FindProperty("playerMarker").objectReferenceValue = player.transform;
            viewSo.FindProperty("mapCamera").objectReferenceValue = cam;
            viewSo.FindProperty("highlight").objectReferenceValue = highlight;
            viewSo.ApplyModifiedPropertiesWithoutUndo();
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

        private static T Require<T>(string path) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) throw new InvalidOperationException($"Missing {typeof(T).Name} at {path}. Is the Mapbox SDK package installed?");
            return asset;
        }

        private static Material PlayerMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(PlayerMaterialPath);
            if (existing != null) return existing;
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(0.95f, 0.25f, 0.2f) };
            System.IO.Directory.CreateDirectory("Assets/_Game/Materials");
            AssetDatabase.CreateAsset(mat, PlayerMaterialPath);
            return mat;
        }

        private static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
