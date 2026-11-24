using System.Collections.Generic;
using UnityEngine;
using ZombieMap.Core.Health;

namespace ZombieMap.Client.Health
{
    /// <summary>
    /// A simple stand-in shape for one body zone, worked out once from the zone's vertices: a tapered
    /// tube along the zone's longest direction, with a rounded end at each side. It is stored as a few
    /// rings of 3D points; projecting them and taking the 2D convex hull gives a clean outline from any
    /// angle, for about 80 points per zone per frame.
    /// </summary>
    public static class ZoneShape
    {
        private const int RingPoints = 12;
        private const int Slices = 3;

        /// <summary>How rounded the ends are (1 = fully round, like a pill).</summary>
        public static float Roundness(BodyPart zone)
        {
            switch (zone)
            {
                case BodyPart.UpperTorso:
                case BodyPart.LowerTorso:
                case BodyPart.Groin:
                    return 0.45f;
                case BodyPart.Neck:
                    return 0.6f;
                default:
                    return 1f;
            }
        }

        /// <summary>Outline points (in the same space as <paramref name="points"/>) for a zone's vertices.</summary>
        public static Vector3[] Build(List<Vector3> points, float roundness)
        {
            if (points.Count < 8) return new Vector3[0];

            var centre = Vector3.zero;
            foreach (var p in points) centre += p;
            centre /= points.Count;

            // Main direction (longest spread), then the other two, from the covariance.
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var p in points)
            {
                var d = p - centre;
                xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z;
                yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
            }
            Vector3 Mul(Vector3 v) => new Vector3(
                xx * v.x + xy * v.y + xz * v.z,
                xy * v.x + yy * v.y + yz * v.z,
                xz * v.x + yz * v.y + zz * v.z);

            var axis = xx >= yy && xx >= zz ? Vector3.right : yy >= zz ? Vector3.up : Vector3.forward;
            for (int i = 0; i < 30; i++) axis = Mul(axis).normalized;
            var u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            for (int i = 0; i < 30; i++)
            {
                u = Mul(u);
                u = (u - Vector3.Dot(u, axis) * axis).normalized; // keep it at right angles to the axis
            }
            var v = Vector3.Cross(axis, u);

            // Length: ignore the odd stray vertex at either end.
            var along = new List<float>(points.Count);
            foreach (var p in points) along.Add(Vector3.Dot(p - centre, axis));
            along.Sort();
            float t0 = Percentile(along, 0.02f), t1 = Percentile(along, 0.98f);
            float length = Mathf.Max(0.001f, t1 - t0);

            // Cross-section (centre and two radii) in a few slices along the length.
            var sliceCentre = new Vector2[Slices];
            var sliceRadius = new Vector2[Slices];
            var us = new List<float>();
            var vs = new List<float>();
            for (int s = 0; s < Slices; s++)
            {
                float a = t0 + length * s / Slices, b = t0 + length * (s + 1) / Slices;
                us.Clear();
                vs.Clear();
                foreach (var p in points)
                {
                    var d = p - centre;
                    float t = Vector3.Dot(d, axis);
                    if (t < a || t > b) continue;
                    us.Add(Vector3.Dot(d, u));
                    vs.Add(Vector3.Dot(d, v));
                }
                if (us.Count < 3)
                {
                    sliceCentre[s] = s > 0 ? sliceCentre[s - 1] : Vector2.zero;
                    sliceRadius[s] = s > 0 ? sliceRadius[s - 1] : new Vector2(0.03f, 0.03f);
                    continue;
                }
                us.Sort();
                vs.Sort();
                float cu = (Percentile(us, 0.05f) + Percentile(us, 0.95f)) / 2;
                float cv = (Percentile(vs, 0.05f) + Percentile(vs, 0.95f)) / 2;
                sliceCentre[s] = new Vector2(cu, cv);
                sliceRadius[s] = new Vector2(Percentile(us, 0.95f) - cu, Percentile(vs, 0.95f) - cv);
            }

            // Rings: rounded cap, the slices, rounded cap.
            var result = new List<Vector3>();
            void Ring(float t, Vector2 c, Vector2 r)
            {
                var mid = centre + axis * t + u * c.x + v * c.y;
                for (int k = 0; k < RingPoints; k++)
                {
                    float ang = k * Mathf.PI * 2 / RingPoints;
                    result.Add(mid + u * (Mathf.Cos(ang) * r.x) + v * (Mathf.Sin(ang) * r.y));
                }
            }
            void Cap(float end, float inward, Vector2 c, Vector2 r)
            {
                float cap = roundness * Mathf.Min((r.x + r.y) / 2, length / 2);
                // Quarter-ellipse profile from the tip in to the full cross-section.
                for (int k = 0; k <= 3; k++)
                {
                    float ang = k * Mathf.PI / 2 / 3;
                    float depth = cap * (1 - Mathf.Cos(ang));
                    float scale = Mathf.Max(0.12f, Mathf.Sin(ang));
                    Ring(end + inward * depth, c, r * scale);
                }
            }
            Cap(t0, 1, sliceCentre[0], sliceRadius[0]);
            for (int s = 0; s < Slices; s++) Ring(t0 + length * (s + 0.5f) / Slices, sliceCentre[s], sliceRadius[s]);
            Cap(t1, -1, sliceCentre[Slices - 1], sliceRadius[Slices - 1]);
            return result.ToArray();
        }

        private static float Percentile(List<float> sorted, float q) =>
            sorted[Mathf.Clamp(Mathf.RoundToInt(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
    }
}
