using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using ZombieMap.Core.Geo;

namespace ZombieMap.Client.Map
{
    /// <summary>
    /// The air over the map: a wind that slowly changes, cloud shadows drifting across by day, dust
    /// motes floating past the camera, and a few burning blocks (firelight on the ground and walls,
    /// embers, and smoke leaning with the wind). Everything is worked out from the real clock and
    /// world position, so every player nearby sees the same clouds and the same fires, with no server.
    /// Kept light for phones: at most four fires, a few hundred particles in all, and the clouds and
    /// firelight are a handful of instructions in shaders that already run.
    /// </summary>
    [DefaultExecutionOrder(110)] // after MapAtmosphere has set the reference point
    public sealed class MapEnvironment : MonoBehaviour
    {
        private const int MaxFires = 4;
        private const double CloudWrapMetres = 4000; // the cloud texture tiles every 4 km
        private const double FireCellLat = 0.003, FireCellLng = 0.0048; // ~330 m
        private const double FireSlotHours = 6;
        private const float FireChance = 0.1f;
        private const double FireSearchMetres = 900;

        private static readonly int CloudId = Shader.PropertyToID("_ZM_Cloud");
        private static readonly int FiresId = Shader.PropertyToID("_ZM_Fires");
        private static readonly int FireCountId = Shader.PropertyToID("_ZM_FireCount");
        private static readonly int CloudTexId = Shader.PropertyToID("_ZM_CloudTex");
        private Texture2D _clouds;

        private sealed class Fire
        {
            public string Key;
            public LatLng Position;
            public float Seed;
            public float Strength; // 0..1 from the fade in and out
            public ParticleSystem Smoke, Embers;
        }

        private MapView _view;
        private MapAtmosphere _atmosphere;
        private MapTimeOfDay _time;
        private Material _smokeMaterial, _glowMaterial;
        private ParticleSystem _dust;
        private readonly Dictionary<string, Fire> _fires = new Dictionary<string, Fire>();
        private readonly List<Fire> _active = new List<Fire>();
        private readonly Vector4[] _fireData = new Vector4[8];
        private float _nextFireSearch;

        /// <summary>Which way the wind blows toward (x east, y north, unit length).</summary>
        public Vector2 WindDirection { get; private set; } = new Vector2(0.8f, 0.6f);
        public float WindSpeed { get; private set; } = 4f;

        public void Init(MapView view, MapAtmosphere atmosphere, MapTimeOfDay time)
        {
            _view = view;
            _atmosphere = atmosphere;
            _time = time;
            var shader = Shader.Find("ZombieMap/Map/Particle");
            _smokeMaterial = new Material(shader);
            _smokeMaterial.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            _smokeMaterial.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            _smokeMaterial.SetFloat("_Softness", 0.9f);
            _glowMaterial = new Material(shader);
            _glowMaterial.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            _glowMaterial.SetFloat("_DstBlend", (float)BlendMode.One);
            _glowMaterial.SetFloat("_Softness", 0.7f);
            _clouds = MakeClouds(256);
            Shader.SetGlobalTexture(CloudTexId, _clouds);
            Shader.SetGlobalVectorArray(FiresId, _fireData);
        }

        private void Update()
        {
            if (_view == null || !_view.IsMapReady || !_view.Location.HasFix)
            {
                Shader.SetGlobalFloat(FireCountId, 0);
                return;
            }
            double t = (DateTime.UtcNow - new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            float day = _time != null ? _time.Day : 0f;
            float mpu = _view.MetresPerUnit;

            Wind(t);
            Clouds(t, day);
            if (Time.unscaledTime >= _nextFireSearch)
            {
                _nextFireSearch = Time.unscaledTime + 5f;
                FindFires(t);
            }
            DrawFires(day, mpu);
            Dust(day, mpu);
        }

        // ---------- wind and clouds ----------

        /// <summary>A breeze that swings slowly through ~50° and gusts a little, the same for everyone.</summary>
        private void Wind(double t)
        {
            double bearing = (60 + 25 * Math.Sin(2 * Math.PI * t / (3 * 3600))) * Math.PI / 180;
            WindDirection = new Vector2((float)Math.Sin(bearing), (float)Math.Cos(bearing));
            WindSpeed = (float)(4 + 1.5 * Math.Sin(2 * Math.PI * t / (47 * 60)));
        }

        /// <summary>Clouds ride a steadier, faster wind higher up; their shadows only show by day.</summary>
        private void Clouds(double t, float day)
        {
            var reference = _atmosphere != null ? _atmosphere.Reference : null;
            if (reference == null) return;
            var r = reference.Value;
            const double cloudBearing = 70 * Math.PI / 180, cloudSpeed = 7;
            // Absolute metres of the reference point, so everyone samples the same cloud field.
            double x = r.Lng * 111320 * Math.Cos(r.Lat * Math.PI / 180) - Math.Sin(cloudBearing) * cloudSpeed * t;
            double y = r.Lat * 110540 - Math.Cos(cloudBearing) * cloudSpeed * t;
            Shader.SetGlobalVector(CloudId, new Vector4((float)Mod(x, CloudWrapMetres), (float)Mod(y, CloudWrapMetres), 0.8f * day, 0));
        }

        // ---------- fires ----------

        /// <summary>
        /// Burning blocks: each ~330 m patch of the world has a one-in-ten chance of a fire in each
        /// six-hour window. They flare up, burn, then die down, so the fires move around through the day.
        /// </summary>
        private void FindFires(double t)
        {
            var here = _view.Location.Position;
            double hours = t / 3600.0;
            long slot = (long)Math.Floor(hours / FireSlotHours);
            double within = hours / FireSlotHours - slot; // 0..1 through the window

            int ci0 = (int)Math.Floor(here.Lat / FireCellLat), cj0 = (int)Math.Floor(here.Lng / FireCellLng);
            int reach = 3;
            var found = new List<(double metres, string key, LatLng pos, float seed)>();
            for (int di = -reach; di <= reach; di++)
            for (int dj = -reach; dj <= reach; dj++)
            {
                int ci = ci0 + di, cj = cj0 + dj;
                if (Hash(ci, cj, slot, 0) > FireChance) continue;
                var pos = new LatLng((ci + 0.15 + 0.7 * Hash(ci, cj, slot, 1)) * FireCellLat, (cj + 0.15 + 0.7 * Hash(ci, cj, slot, 2)) * FireCellLng);
                double metres = GeoMath.Distance(here, pos);
                if (metres <= FireSearchMetres) found.Add((metres, $"{ci},{cj},{slot}", pos, Hash(ci, cj, slot, 3) * 100f));
            }
            found.Sort((a, b) => a.metres.CompareTo(b.metres));

            var keep = new HashSet<string>();
            for (int i = 0; i < found.Count && i < MaxFires; i++)
            {
                var f = found[i];
                keep.Add(f.key);
                if (!_fires.ContainsKey(f.key))
                    _fires[f.key] = new Fire { Key = f.key, Position = f.pos, Seed = f.seed };
            }
            // Flare up over the first ~35 minutes of the window, die down over the last ~35.
            float strength = Mathf.Clamp01((float)Math.Min(within, 1 - within) / 0.1f);
            foreach (var f in _fires.Values) f.Strength = keep.Contains(f.Key) ? strength : 0f;

            var gone = new List<string>();
            foreach (var f in _fires.Values)
                if (!keep.Contains(f.Key)) gone.Add(f.Key);
            foreach (var key in gone)
            {
                var f = _fires[key];
                if (f.Smoke != null) Destroy(f.Smoke.gameObject);
                if (f.Embers != null) Destroy(f.Embers.gameObject);
                _fires.Remove(key);
            }
        }

        private void DrawFires(float day, float mpu)
        {
            _active.Clear();
            _active.AddRange(_fires.Values);
            int n = 0;
            var wind = new Vector3(WindDirection.x, 0, WindDirection.y) * WindSpeed / mpu;
            foreach (var f in _active)
            {
                var world = _view.ToWorld(f.Position);
                if (f.Smoke == null) Create(f, world, mpu);
                f.Smoke.transform.position = world;
                f.Embers.transform.position = world;

                // Smoke leans with the wind: dark grey by day, lit from below by the flames at night.
                var main = f.Smoke.main;
                main.startColor = Color.Lerp(new Color(0.3f, 0.14f, 0.07f, 0.55f), new Color(0.2f, 0.2f, 0.22f, 0.5f), day) * new Color(1, 1, 1, f.Strength);
                var sv = f.Smoke.velocityOverLifetime;
                sv.x = wind.x * 0.7f;
                sv.y = 2.2f / mpu;
                sv.z = wind.z * 0.7f;
                var ev = f.Embers.velocityOverLifetime;
                ev.x = new ParticleSystem.MinMaxCurve(wind.x * 0.4f - 0.8f / mpu, wind.x * 0.4f + 0.8f / mpu);
                ev.y = new ParticleSystem.MinMaxCurve(2.5f / mpu, 5f / mpu);
                ev.z = new ParticleSystem.MinMaxCurve(wind.z * 0.4f - 0.8f / mpu, wind.z * 0.4f + 0.8f / mpu);
                var emission = f.Embers.emission;
                emission.rateOverTime = 12f * f.Strength;
                var smokeEmission = f.Smoke.emission;
                smokeEmission.rateOverTime = 5f * f.Strength;

                if (n < 8)
                {
                    // Flames flicker: two out-of-step waves and a little noise.
                    float flicker = 0.75f + 0.15f * Mathf.Sin(Time.time * 7.3f + f.Seed) + 0.1f * Mathf.PerlinNoise(Time.time * 3f, f.Seed);
                    _fireData[n++] = new Vector4(world.x, world.y, world.z, f.Strength * flicker);
                }
            }
            Shader.SetGlobalVectorArray(FiresId, _fireData);
            Shader.SetGlobalFloat(FireCountId, n);
        }

        private void Create(Fire f, Vector3 world, float mpu)
        {
            f.Smoke = Particles("FireSmoke", world, _smokeMaterial, max: 60);
            var main = f.Smoke.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(9f, 14f);
            main.startSize = new ParticleSystem.MinMaxCurve(12f / mpu, 22f / mpu);
            var shape = f.Smoke.shape;
            shape.radius = 5f / mpu;
            var size = f.Smoke.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 0.5f, 1, 2.4f));
            Fade(f.Smoke, 0.15f);
            f.Smoke.Play();

            f.Embers = Particles("FireEmbers", world, _glowMaterial, max: 40);
            var em = f.Embers.main;
            em.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 3f);
            em.startSize = new ParticleSystem.MinMaxCurve(0.7f / mpu, 1.4f / mpu);
            em.startColor = new Color(1f, 0.55f, 0.18f, 0.7f);
            var es = f.Embers.shape;
            es.radius = 4f / mpu;
            Fade(f.Embers, 0.05f);
            f.Embers.Play();
        }

        /// <summary>Dev: light a fire a little way from the player, so fires can be checked without waiting for one.</summary>
        public void SpawnTestFire()
        {
            if (_view == null || !_view.Location.HasFix) return;
            var pos = GeoMath.Offset(_view.Location.Position, 60, 40);
            _fires["test"] = new Fire { Key = "test", Position = pos, Seed = 42f, Strength = 1f };
            _nextFireSearch = float.MaxValue; // keep it until the next play session
        }

        // ---------- dust ----------

        /// <summary>A few motes drifting through the air in front of the camera, carried by the wind.</summary>
        private void Dust(float day, float mpu)
        {
            var cam = _view.MapCamera;
            if (cam == null) return;
            if (_dust == null)
            {
                _dust = Particles("Dust", Vector3.zero, _glowMaterial, max: 260);
                var main = _dust.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(7f, 11f);
                var shape = _dust.shape;
                shape.shapeType = ParticleSystemShapeType.Box;
                shape.rotation = Vector3.zero;
                Fade(_dust, 0.2f);
                _dust.Play();
            }
            float distance = Vector3.Distance(cam.transform.position, _view.FocusPoint);
            // Fill the space between the camera and the ground, around what you're looking at.
            _dust.transform.position = Vector3.Lerp(_view.FocusPoint, cam.transform.position, 0.35f);
            _dust.transform.rotation = Quaternion.Euler(0, cam.transform.eulerAngles.y, 0);
            var box = _dust.shape;
            box.scale = new Vector3(distance * 1.4f, distance * 0.5f, distance * 1.2f);

            float twilight = _time != null ? _time.Twilight : 0f;
            var colour = Color.Lerp(new Color(0.55f, 0.65f, 0.9f, 0.2f), new Color(0.9f, 0.9f, 0.85f, 0.24f), day);
            colour = Color.Lerp(colour, new Color(1f, 0.7f, 0.45f, 0.24f), twilight * 0.8f);
            var dm = _dust.main;
            dm.startColor = colour;
            dm.startSize = new ParticleSystem.MinMaxCurve(distance * 0.0025f, distance * 0.005f);
            var emission = _dust.emission;
            emission.rateOverTime = 26f;
            var vel = _dust.velocityOverLifetime;
            var wind = new Vector3(WindDirection.x, 0, WindDirection.y) * WindSpeed / mpu;
            vel.x = new ParticleSystem.MinMaxCurve(wind.x * 0.8f, wind.x * 1.2f);
            vel.y = new ParticleSystem.MinMaxCurve(-0.2f / mpu, 0.4f / mpu);
            vel.z = new ParticleSystem.MinMaxCurve(wind.z * 0.8f, wind.z * 1.2f);
        }

        // ---------- cloud texture ----------

        /// <summary>
        /// Seamless cloud cover for one 4 km tile: domain-warped, layered noise (so the shapes curl and
        /// billow instead of sitting on a grid), made once at startup and read once per pixel.
        /// </summary>
        private static Texture2D MakeClouds(int size)
        {
            var t = new Texture2D(size, size, TextureFormat.R8, true, true)
            {
                name = "ZM_Clouds", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 2,
            };
            var px = new byte[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (float)x / size, v = (float)y / size;
                float wx = TileFbm(u, v, 3, 2, 11) - 0.5f, wy = TileFbm(u, v, 3, 2, 23) - 0.5f;
                float n = TileFbm(u + wx * 0.22f, v + wy * 0.22f, 5, 5, 7);
                px[y * size + x] = (byte)Mathf.RoundToInt(Mathf.Clamp01(n) * 255f);
            }
            t.SetPixelData(px, 0);
            t.Apply(true, true);
            return t;
        }

        /// <summary>Layered value noise that tiles over 0..1 (each layer's lattice repeats exactly).</summary>
        private static float TileFbm(float u, float v, int period, int octaves, int seed)
        {
            float sum = 0, amp = 0.5f, total = 0;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * TileNoise(u, v, period << o, seed + o * 31);
                total += amp;
                amp *= 0.5f;
            }
            return sum / total;
        }

        private static float TileNoise(float u, float v, int period, int seed)
        {
            float fx = u * period, fy = v * period;
            int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy);
            float tx = fx - ix, ty = fy - iy;
            // Quintic easing: no visible grid creases.
            tx = tx * tx * tx * (tx * (tx * 6 - 15) + 10);
            ty = ty * ty * ty * (ty * (ty * 6 - 15) + 10);
            int x0 = Wrap(ix, period), x1 = Wrap(ix + 1, period), y0 = Wrap(iy, period), y1 = Wrap(iy + 1, period);
            float a = Hash(x0, y0, seed, 9), b = Hash(x1, y0, seed, 9), c = Hash(x0, y1, seed, 9), d = Hash(x1, y1, seed, 9);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        private static int Wrap(int a, int n) => ((a % n) + n) % n;

        // ---------- helpers ----------

        private ParticleSystem Particles(string name, Vector3 at, Material material, int max)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.position = at;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f;
            main.maxParticles = max;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.rotation = new Vector3(90, 0, 0); // flat on the ground
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = material;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        /// <summary>Fade in over the first part of a particle's life and out by the end.</summary>
        private static void Fade(ParticleSystem ps, float fadeIn)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, fadeIn), new GradientAlphaKey(0, 1) });
            col.color = g;
        }

        private static double Mod(double a, double m) => ((a % m) + m) % m;

        private static float Hash(int a, int b, long c, int salt)
        {
            unchecked
            {
                uint h = (uint)(a * 374761393 + b * 668265263 + (int)c * 1274126177 + salt * 144665);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }

        private void OnDisable()
        {
            Shader.SetGlobalFloat(FireCountId, 0);
            Shader.SetGlobalVector(CloudId, Vector4.zero);
        }

        private void OnDestroy()
        {
            if (_clouds != null) Destroy(_clouds);
        }
    }
}
