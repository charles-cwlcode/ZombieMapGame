using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mapbox.BaseModule.Unity;
using Mapbox.Example.Scripts.Map;
using Mapbox.VectorModule.ComponentSystem;
using Mapbox.VectorModule.ComponentSystem.AreaComponent;
using Mapbox.VectorModule.ComponentSystem.BuildingComponentVisualizer;
using Mapbox.VectorModule.ComponentSystem.RoadComponentVisualizer;
using Mapbox.VectorModule.Filters;
using Mapbox.VectorModule.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ZombieMap.Client.Map;

namespace ZombieMap.Client.Editor
{
    /// <summary>
    /// Builds the map look from code: a dark navy "command map". Navy ground with a survey grid,
    /// slate-blue buildings, roads as lines (main roads glowing amber, streets thin blue-grey), bloom,
    /// vignette and a tilt-shift blur. Creates the materials for the ZombieMap/Map/* shaders, the Mapbox
    /// vector layer assets, the post-processing profile, and adds the tilt-shift pass to the URP
    /// renderers. Re-run it after changing a colour or width here; it updates the assets in place.
    /// </summary>
    public static class MapStyleBuilder
    {
        private const string Folder = "Assets/_Game/Map/Style";

        // Render order for the flat layers (they don't write depth; later draws on top).
        private const int QGround = 1900, QPlaza = 1910, QPark = 1920, QWater = 1940,
            QPath = 1955, QService = 1957, QStreet = 1958, QSecondary = 1962, QMajor = 1965;

        // Materials from earlier looks that nothing uses any more.
        private static readonly string[] Obsolete =
        {
            "Paved", "Industrial", "Civic", "Overgrown", "DeadGrass", "Pitch", "Wood", "Field", "Sand",
            "Pavement", "Pedestrian", "DirtPath",
        };

        [MenuItem("ZombieMap/Map Style/Build Assets and Apply to Scene")]
        public static void BuildAndApplyMenu() => Debug.Log(BuildAndApply());

        [MenuItem("ZombieMap/Map Style/Build Assets Only")]
        public static void BuildAssetsMenu() => Debug.Log(BuildAssets().Log);

        public static string BuildAndApply()
        {
            var a = BuildAssets();
            return a.Log + "\n" + ApplyToScene(a);
        }

        // ---------- assets ----------

        public sealed class StyleAssets
        {
            public Material Ground;
            public List<LayerVisualizerConstructor> Visualizers = new List<LayerVisualizerConstructor>();
            public VolumeProfile Post;
            public string Log = "";
        }

        public static StyleAssets BuildAssets()
        {
            Directory.CreateDirectory(Folder);
            var a = new StyleAssets();
            var log = new List<string>();

            var ground = Shader.Find("ZombieMap/Map/Ground");
            var road = Shader.Find("ZombieMap/Map/Road");
            var building = Shader.Find("ZombieMap/Map/Building");
            if (ground == null || road == null || building == null)
                throw new System.InvalidOperationException("Map shaders not found: are there compile errors in Assets/_Game/Shaders/Map?");

            foreach (var name in Obsolete)
                if (AssetDatabase.DeleteAsset($"{Folder}/{name}.mat")) log.Add("deleted " + name);

            // Flat surfaces: navy ground, faintly teal parks, slightly lighter plazas, near-black water.
            a.Ground = Mat("Ground", ground, QGround, null);
            var park = Mat("Park", ground, QPark, m =>
            {
                m.SetColor("_Color", C(0.045f, 0.095f, 0.115f));
                m.SetColor("_FocusColor", C(0.065f, 0.135f, 0.155f));
                m.SetColor("_ColorDay", C(0.56f, 0.62f, 0.57f));
                m.SetColor("_FocusColorDay", C(0.61f, 0.67f, 0.62f));
            });
            var plaza = Mat("Plaza", ground, QPlaza, m =>
            {
                m.SetColor("_Color", C(0.075f, 0.11f, 0.17f));
                m.SetColor("_FocusColor", C(0.11f, 0.155f, 0.23f));
                m.SetColor("_ColorDay", C(0.67f, 0.67f, 0.68f));
                m.SetColor("_FocusColorDay", C(0.72f, 0.72f, 0.73f));
            });
            var waterMat = Mat("Water", ground, QWater, m =>
            {
                m.SetColor("_Color", C(0.018f, 0.035f, 0.065f));
                m.SetColor("_FocusColor", C(0.03f, 0.055f, 0.095f));
                m.SetColor("_ColorDay", C(0.44f, 0.52f, 0.60f));
                m.SetColor("_FocusColorDay", C(0.47f, 0.55f, 0.63f));
                m.SetFloat("_CrossAlpha", 0.2f);
            });

            // Roads: (name, classes, mesh width m, line width m, colour at night, colour by day, halo, min px, dash m, opacity, queue).
            var roadTypes = new (string name, string classes, float mesh, float line, Color colour, Color day, float halo, float minPx, float dash, float alpha, int queue)[]
            {
                ("Major", "motorway,trunk,primary,motorway_link,trunk_link,primary_link", 22f, 5f, H(1.0f, 0.72f, 0.1f, 1.9f), C(0.98f, 0.72f, 0.2f), 0.4f, 2.5f, 0f, 1f, QMajor),
                ("Secondary", "secondary,tertiary,secondary_link,tertiary_link", 14f, 2.8f, H(1.0f, 0.78f, 0.25f, 1.05f), C(0.97f, 0.8f, 0.42f), 0.12f, 2f, 0f, 1f, QSecondary),
                ("Street", "street,street_limited", 7f, 1.2f, C(0.30f, 0.40f, 0.55f), C(0.90f, 0.905f, 0.91f), 0f, 1.5f, 0f, 0.75f, QStreet),
                ("Service", "service,construction", 4.5f, 0.8f, C(0.20f, 0.27f, 0.38f), C(0.84f, 0.85f, 0.86f), 0f, 1f, 0f, 0.45f, QService),
                ("Path", "path,track,pedestrian", 3f, 0.6f, C(0.22f, 0.29f, 0.40f), C(0.78f, 0.79f, 0.81f), 0f, 1f, 3f, 0.25f, QPath),
            };
            var fill = new List<RoadStyle>();
            foreach (var r in roadTypes)
            {
                var mat = Mat("Road" + r.name, road, r.queue, m =>
                {
                    m.SetColor("_Color", r.colour);
                    m.SetColor("_ColorDay", r.day);
                    m.SetFloat("_WidthMetres", r.mesh);
                    m.SetFloat("_LineMetres", r.line);
                    m.SetFloat("_MinPixels", r.minPx);
                    m.SetFloat("_Halo", r.halo);
                    m.SetFloat("_Dash", r.dash);
                    m.SetFloat("_Alpha", r.alpha);
                });
                fill.Add(Road(r.name, r.classes, r.mesh, mat));
            }

            var roadSheet = Asset<RoadStyleSheet>("RoadStyleSheet");
            roadSheet.Styles = fill;
            EditorUtility.SetDirty(roadSheet);

            // Land use, by Mapbox Streets v8 class.
            var landuse = Asset<AreaComponentVisualizerObject>("LanduseLayer");
            landuse.Settings = new AreaComponentSettings
            {
                LayerName = "landuse",
                GameObjectOffset = 0f,
                Styles = new List<AreaStyle>
                {
                    Area("commercial_area", plaza), Area("parking", plaza), Area("facility", plaza), Area("airport", plaza),
                    Area("industrial", plaza), Area("school", plaza), Area("hospital", plaza),
                    Area("park", park), Area("grass", park), Area("pitch", park), Area("cemetery", park),
                    Area("scrub", park), Area("wood", park), Area("agriculture", park),
                }
            };
            landuse.IsActive = true;
            EditorUtility.SetDirty(landuse);

            var waterLayer = Asset<AreaComponentVisualizerObject>("WaterLayer");
            waterLayer.Settings = new AreaComponentSettings
            {
                LayerName = "water",
                GameObjectOffset = 0f,
                Styles = new List<AreaStyle> { Area("water", waterMat) }
            };
            waterLayer.IsActive = true;
            EditorUtility.SetDirty(waterLayer);

            // No pavements in this look; the layer stays (the scene references it) but is switched off.
            var pavementSheet = Asset<RoadStyleSheet>("PavementStyleSheet");
            pavementSheet.Styles = new List<RoadStyle>();
            EditorUtility.SetDirty(pavementSheet);
            var pavementLayer = Asset<RoadComponentVisualizerObject>("PavementLayer");
            pavementLayer.Settings = new RoadComponentSettings { GameObjectOffset = 0f, RandomOffsetRange = 0f, RoadStyleSheet = pavementSheet };
            pavementLayer.IsActive = false;
            EditorUtility.SetDirty(pavementLayer);

            var roadLayer = Asset<RoadComponentVisualizerObject>("RoadLayer");
            roadLayer.Settings = new RoadComponentSettings { GameObjectOffset = 0f, RandomOffsetRange = 0f, RoadStyleSheet = roadSheet };
            roadLayer.IsActive = true;
            EditorUtility.SetDirty(roadLayer);

            var buildingMat = Mat("Building", building, 2000, null);
            var buildings = Asset<BuildingComponentVisualizerObject>("BuildingLayer");
            buildings.Settings ??= new BuildingComponentSettings();
            buildings.Settings.Material = buildingMat;
            buildings.Settings.RoundBuildingCorners = false;
            buildings.Settings.EnableTerrainSnapping = false;
            buildings.IsActive = true;
            EditorUtility.SetDirty(buildings);

            a.Visualizers.AddRange(new LayerVisualizerConstructor[] { landuse, waterLayer, pavementLayer, roadLayer, buildings });
            a.Post = BuildPostProfile();
            log.Add(AddFeature<TiltShiftFeature>("TiltShift", "Hidden/ZombieMap/TiltShift"));
            log.Add(AddFeature<MapOutlineFeature>("MapOutline", "Hidden/ZombieMap/MapOutline"));

            AssetDatabase.SaveAssets();
            a.Log = "Map style assets rebuilt: " + string.Join(", ", log);
            return a;
        }

        private static VolumeProfile BuildPostProfile()
        {
            var profile = Asset<VolumeProfile>("MapPostFX");
            // Start clean each build.
            foreach (var c in profile.components.ToList())
            {
                profile.components.Remove(c);
                Object.DestroyImmediate(c, true);
            }

            T Add<T>() where T : VolumeComponent
            {
                var c = profile.Add<T>(true);
                c.name = typeof(T).Name;
                c.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(c, profile);
                return c;
            }

            var bloom = Add<Bloom>();
            bloom.threshold.Override(0.95f);
            bloom.intensity.Override(1.1f);
            bloom.scatter.Override(0.65f);

            var grade = Add<ColorAdjustments>();
            grade.contrast.Override(8f);
            grade.saturation.Override(6f);

            var vignette = Add<Vignette>();
            vignette.intensity.Override(0.38f);
            vignette.smoothness.Override(0.5f);
            vignette.color.Override(new Color(0f, 0.01f, 0.03f));

            EditorUtility.SetDirty(profile);
            return profile;
        }

        /// <summary>Adds a renderer feature to every URP renderer in Assets/Settings (once).</summary>
        private static string AddFeature<T>(string name, string shaderName) where T : ScriptableRendererFeature
        {
            var shader = Shader.Find(shaderName);
            var added = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData", new[] { "Assets/Settings" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (data == null || data.rendererFeatures.Any(f => f is T)) continue;

                var feature = ScriptableObject.CreateInstance<T>();
                feature.name = name;
                var fso = new SerializedObject(feature);
                fso.FindProperty("shader").objectReferenceValue = shader;
                fso.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.AddObjectToAsset(feature, data);

                // Mirror what the renderer's own "Add Renderer Feature" button does.
                var so = new SerializedObject(data);
                var features = so.FindProperty("m_RendererFeatures");
                var map = so.FindProperty("m_RendererFeatureMap");
                features.arraySize++;
                features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
                map.arraySize++;
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(data);
                added.Add(Path.GetFileNameWithoutExtension(path));
            }
            return added.Count == 0 ? name + " already on the renderers" : name + " added to " + string.Join(", ", added);
        }

        // ---------- scene ----------

        public static string ApplyToScene(StyleAssets a)
        {
            var mapBehaviour = Object.FindAnyObjectByType<MapboxMapBehaviour>();
            if (mapBehaviour == null) return "No MapboxMapBehaviour in the open scene: open Assets/_Game/Scenes/Dev.unity first.";
            var go = mapBehaviour.gameObject;
            var log = new List<string>();

            // Raster street tiles and the old building module go; the vector components module draws everything.
            foreach (var module in go.GetComponents<ModuleConstructorScript>())
            {
                if (module is MapboxComponentsModuleScript) continue;
                log.Add("removed " + module.GetType().Name);
                Undo.DestroyObjectImmediate(module);
            }
            var components = go.GetComponent<MapboxComponentsModuleScript>() ?? Undo.AddComponent<MapboxComponentsModuleScript>(go);
            var so = new SerializedObject(components);
            var list = so.FindProperty("_layerVisualizers");
            list.arraySize = a.Visualizers.Count;
            for (int i = 0; i < a.Visualizers.Count; i++) list.GetArrayElementAtIndex(i).objectReferenceValue = a.Visualizers[i];
            so.ApplyModifiedProperties();

            var mb = new SerializedObject(mapBehaviour);
            mb.FindProperty("_defaultTileMaterial").objectReferenceValue = a.Ground;
            mb.ApplyModifiedProperties();

            var view = Object.FindAnyObjectByType<MapView>();
            var atmosphere = Object.FindAnyObjectByType<MapAtmosphere>();
            if (atmosphere == null && view != null)
            {
                atmosphere = Undo.AddComponent<MapAtmosphere>(view.gameObject);
                log.Add("added MapAtmosphere");
            }
            if (atmosphere != null)
            {
                var aso = new SerializedObject(atmosphere);
                aso.FindProperty("view").objectReferenceValue = view;
                aso.ApplyModifiedProperties();
            }

            var volume = Object.FindAnyObjectByType<Volume>();
            if (volume == null)
            {
                var vgo = new GameObject("MapPostFX");
                Undo.RegisterCreatedObjectUndo(vgo, "Map post FX");
                volume = vgo.AddComponent<Volume>();
                log.Add("added MapPostFX volume");
            }
            volume.isGlobal = true;
            volume.sharedProfile = a.Post;
            EditorUtility.SetDirty(volume);

            if (view != null && view.MapCamera != null)
            {
                var camData = view.MapCamera.GetUniversalAdditionalCameraData();
                camData.renderPostProcessing = true;
                EditorUtility.SetDirty(camData);
            }

            var scene = go.scene;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            log.Add("saved " + scene.path);
            return "Map style applied: " + string.Join(", ", log);
        }

        // ---------- helpers ----------

        private static Color C(float r, float g, float b) => new Color(r, g, b, 1f);

        /// <summary>HDR colour: a base colour times an intensity, so bloom picks it up.</summary>
        private static Color H(float r, float g, float b, float intensity) => new Color(r * intensity, g * intensity, b * intensity, 1f);

        private static Material Mat(string name, Shader shader, int queue, System.Action<Material> setup)
        {
            string path = $"{Folder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            // Start from the shader defaults every build, so old settings never linger.
            var fresh = new Material(shader);
            m.shader = shader;
            m.CopyPropertiesFromMaterial(fresh);
            m.shaderKeywords = new string[0];
            Object.DestroyImmediate(fresh);
            setup?.Invoke(m);
            m.renderQueue = queue;
            EditorUtility.SetDirty(m);
            return m;
        }

        private static T Asset<T>(string name) where T : ScriptableObject
        {
            string path = $"{Folder}/{name}.asset";
            var a = AssetDatabase.LoadAssetAtPath<T>(path);
            if (a != null) return a;
            a = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(a, path);
            return a;
        }

        private static AreaStyle Area(string cls, Material m) => new AreaStyle { ClassName = cls, Material = m, PushUp = 0f };

        private static RoadStyle Road(string name, string classes, float width, Material m) => new RoadStyle
        {
            Name = name,
            Width = width,
            PushUp = 0f,
            Material = m,
            Filters = new ComponentFilterStack
            {
                Type = LayerFilterCombinerOperationType.Any,
                Filters = new List<RoadFilter> { new RoadClassFilter { CheckOperation = StringCheckOperation.Contains, FilterString = classes } }
            }
        };
    }
}
