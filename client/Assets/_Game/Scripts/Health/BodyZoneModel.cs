using System;
using System.Collections.Generic;
using UnityEngine;
using ZombieMap.Core.Health;

namespace ZombieMap.Client.Health
{
    /// <summary>
    /// Splits a Character Creator (CC4/CC5) body into the game's 17 body zones, using the skeleton:
    /// each vertex belongs to the zone of the bone that moves it most, and each triangle to the zone
    /// most of its corners are in. The result is one static mesh with a submesh (and material) per
    /// zone, so zones can be coloured and tapped on their own. Works with any CC export, so a new
    /// AI/CC5 character only needs dropping in.
    /// </summary>
    public sealed class BodyZoneModel : MonoBehaviour
    {
        public static readonly BodyPart[] Zones = (BodyPart[])Enum.GetValues(typeof(BodyPart));

        [Tooltip("Name of the body mesh in the CC export.")]
        [SerializeField] private string bodyMeshName = "CC_Base_Body";

        private MeshRenderer _renderer;
        private Material[] _materials;
        private BodyPart[] _triangleZone;
        private readonly Vector3[] _localAnchors = new Vector3[Zones.Length];
        private readonly bool[] _hasZone = new bool[Zones.Length];
        private readonly Vector3[][] _shapes = new Vector3[Zones.Length][];

        public bool IsBuilt => _renderer != null;
        public Bounds LocalBounds { get; private set; }
        public Transform Mesh => _renderer != null ? _renderer.transform : null;

        /// <summary>Builds the zone mesh from a CC character instance (its renderers are switched off).</summary>
        public void Build(GameObject character, Material baseMaterial, int layer)
        {
            var source = FindBody(character);
            if (source == null)
            {
                Debug.LogError($"[BodyZoneModel] No skinned mesh in {character.name}.");
                return;
            }
            if (!source.sharedMesh.isReadable)
            {
                Debug.LogError($"[BodyZoneModel] Turn on Read/Write in the import settings of the model for '{source.sharedMesh.name}'.");
                return;
            }

            var baked = new Mesh { indexFormat = source.sharedMesh.indexFormat };
            // Bake in the renderer's own (unscaled) space: the zone object is parented under the same transform,
            // which applies its scale once. Baking with useScale too would scale the body twice.
            source.BakeMesh(baked, false);
            var vertexZone = VertexZones(source);

            // All triangles of the body.
            var source3 = source.sharedMesh;
            var all = new List<int>();
            for (int sub = 0; sub < source3.subMeshCount; sub++) all.AddRange(source3.GetTriangles(sub));

            // Group triangles by zone (majority of the three corners; ties go to the first corner).
            var perZone = new List<int>[Zones.Length];
            for (int z = 0; z < perZone.Length; z++) perZone[z] = new List<int>();
            for (int t = 0; t < all.Count; t += 3)
            {
                int a = all[t], b = all[t + 1], c = all[t + 2];
                var zone = vertexZone[b] == vertexZone[c] ? vertexZone[b] : vertexZone[a];
                var list = perZone[(int)zone];
                list.Add(a);
                list.Add(b);
                list.Add(c);
            }

            var mesh = new Mesh { name = "BodyZones", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(baked.vertices);
            mesh.SetNormals(baked.normals);
            mesh.subMeshCount = perZone.Length;
            // Triangle order for picking follows submesh order.
            var triangleZones = new List<BodyPart>();
            for (int z = 0; z < perZone.Length; z++)
            {
                mesh.SetTriangles(perZone[z], z);
                for (int i = 0; i < perZone[z].Count / 3; i++) triangleZones.Add(Zones[z]);
            }
            mesh.RecalculateBounds();
            _triangleZone = triangleZones.ToArray();
            Destroy(baked);

            // Anchor for each zone's callout line: the middle of its vertices.
            var verts = mesh.vertices;
            var sums = new Vector3[Zones.Length];
            var counts = new int[Zones.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                sums[(int)vertexZone[i]] += verts[i];
                counts[(int)vertexZone[i]]++;
            }
            for (int z = 0; z < Zones.Length; z++)
            {
                _hasZone[z] = counts[z] > 0;
                _localAnchors[z] = counts[z] > 0 ? sums[z] / counts[z] : mesh.bounds.center;
            }
            LocalBounds = mesh.bounds;

            // A simple 2D-friendly stand-in shape per zone, for the health overlay.
            var zoneVerts = new List<Vector3>[Zones.Length];
            for (int z = 0; z < Zones.Length; z++) zoneVerts[z] = new List<Vector3>();
            for (int i = 0; i < verts.Length; i++) zoneVerts[(int)vertexZone[i]].Add(verts[i]);
            for (int z = 0; z < Zones.Length; z++) _shapes[z] = ZoneShape.Build(zoneVerts[z], ZoneShape.Roundness(Zones[z]));

            var go = new GameObject("BodyZones") { layer = layer };
            go.transform.SetParent(source.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            _renderer = go.AddComponent<MeshRenderer>();
            _materials = new Material[Zones.Length];
            for (int z = 0; z < Zones.Length; z++) _materials[z] = new Material(baseMaterial) { name = Zones[z].ToString() };
            _renderer.sharedMaterials = _materials;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;

            foreach (var r in character.GetComponentsInChildren<Renderer>(true))
                if (r != _renderer) r.enabled = false;
        }

        /// <summary>Projects the zone's stand-in shape to screen pixels (y up). Points behind the camera are skipped.</summary>
        public void ShapePoints(BodyPart zone, Camera cam, List<Vector2> into)
        {
            into.Clear();
            var shape = _shapes[(int)zone];
            if (_renderer == null || shape == null) return;
            var m = _renderer.transform.localToWorldMatrix;
            foreach (var p in shape)
            {
                var s = cam.WorldToScreenPoint(m.MultiplyPoint3x4(p));
                if (s.z > 0) into.Add(new Vector2(s.x, s.y));
            }
        }

        public void SetColor(BodyPart zone, Color color)
        {
            if (_materials == null) return;
            Tint(_materials[(int)zone], color);
        }

        private static void Tint(Material m, Color color)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            else m.color = color;
        }

        public bool HasZone(BodyPart zone) => _hasZone[(int)zone];

        /// <summary>World position to point a callout line at.</summary>
        public Vector3 Anchor(BodyPart zone) =>
            _renderer != null ? _renderer.transform.TransformPoint(_localAnchors[(int)zone]) : transform.position;

        /// <summary>Which zone a ray hits, if any (for tapping the body).</summary>
        public bool TryPick(Ray ray, out BodyPart zone)
        {
            zone = default;
            if (_renderer == null || !Physics.Raycast(ray, out var hit, 100f, 1 << _renderer.gameObject.layer)) return false;
            if (hit.collider.gameObject != _renderer.gameObject || hit.triangleIndex < 0 || hit.triangleIndex >= _triangleZone.Length) return false;
            zone = _triangleZone[hit.triangleIndex];
            return true;
        }

        private SkinnedMeshRenderer FindBody(GameObject character)
        {
            SkinnedMeshRenderer best = null;
            foreach (var smr in character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                if (smr.name == bodyMeshName) return smr;
                if (best == null || smr.sharedMesh.vertexCount > best.sharedMesh.vertexCount) best = smr;
            }
            return best;
        }

        private static BodyPart[] VertexZones(SkinnedMeshRenderer smr)
        {
            var mesh = smr.sharedMesh;
            var boneZone = new BodyPart[smr.bones.Length];
            for (int b = 0; b < smr.bones.Length; b++) boneZone[b] = ZoneForBone(smr.bones[b]);

            var zones = new BodyPart[mesh.vertexCount];
            var perVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            int w = 0;
            var sum = new float[Zones.Length];
            for (int v = 0; v < zones.Length; v++)
            {
                int n = perVertex.Length > 0 ? perVertex[v] : 0;
                float best = -1;
                var zone = BodyPart.UpperTorso;
                // Sum weights per zone: twist bones and fingers add up with their parent zone.
                Array.Clear(sum, 0, sum.Length);
                for (int i = 0; i < n; i++, w++)
                {
                    var bw = weights[w];
                    if (bw.boneIndex < boneZone.Length) sum[(int)boneZone[bw.boneIndex]] += bw.weight;
                }
                for (int z = 0; z < sum.Length; z++)
                    if (sum[z] > best)
                    {
                        best = sum[z];
                        zone = Zones[z];
                    }
                zones[v] = zone;
            }
            return zones;
        }

        /// <summary>CC bone name → zone. Unknown bones use their nearest known parent.</summary>
        public static BodyPart ZoneForBone(Transform bone)
        {
            for (var t = bone; t != null; t = t.parent)
                if (TryZone(t.name, out var zone)) return zone;
            return BodyPart.UpperTorso;
        }

        public static bool TryZone(string boneName, out BodyPart zone)
        {
            string n = boneName.ToLowerInvariant();
            zone = default;
            if (Side(n, "upperarm", BodyPart.LeftUpperArm, BodyPart.RightUpperArm, out zone)) return true;
            if (Side(n, "forearm", BodyPart.LeftForearm, BodyPart.RightForearm, out zone) || Side(n, "elbow", BodyPart.LeftForearm, BodyPart.RightForearm, out zone)) return true;
            // Feet before fingers: CC names toes like fingers (CC_Base_L_IndexToe1, CC_Base_L_MidToe1).
            if (Side(n, "foot", BodyPart.LeftFoot, BodyPart.RightFoot, out zone) || Side(n, "toe", BodyPart.LeftFoot, BodyPart.RightFoot, out zone)) return true;
            foreach (var finger in new[] { "hand", "thumb", "index", "mid", "ring", "pinky" })
                if (Side(n, finger, BodyPart.LeftHand, BodyPart.RightHand, out zone)) return true;
            if (Side(n, "thigh", BodyPart.LeftThigh, BodyPart.RightThigh, out zone)) return true;
            if (Side(n, "calf", BodyPart.LeftShin, BodyPart.RightShin, out zone) || Side(n, "knee", BodyPart.LeftShin, BodyPart.RightShin, out zone)) return true;
            // Limbs first: "forearm" contains "ear".
            if (Has(n, "neck")) { zone = BodyPart.Neck; return true; }
            if (Has(n, "head", "jaw", "eye", "teeth", "tongue", "facial", "brow", "cheek", "nose", "lip", "mouth") || n.EndsWith("ear")) { zone = BodyPart.Head; return true; }
            if (Has(n, "spine02", "breast", "clavicle", "chest")) { zone = BodyPart.UpperTorso; return true; }
            if (Has(n, "spine01", "waist", "spine")) { zone = BodyPart.LowerTorso; return true; }
            if (Has(n, "hip", "pelvis")) { zone = BodyPart.Groin; return true; }
            return false;
        }

        private static bool Has(string n, params string[] parts)
        {
            foreach (var p in parts) if (n.Contains(p)) return true;
            return false;
        }

        /// <summary>CC names sides "_L_" / "_R_" (e.g. CC_Base_L_Forearm, CC_Base_R_ThighTwist01).</summary>
        private static bool Side(string n, string part, BodyPart left, BodyPart right, out BodyPart zone)
        {
            zone = default;
            if (!n.Contains(part)) return false;
            if (n.Contains("_l_") || n.StartsWith("l_") || n.EndsWith("_l")) { zone = left; return true; }
            if (n.Contains("_r_") || n.StartsWith("r_") || n.EndsWith("_r")) { zone = right; return true; }
            return false;
        }
    }
}
