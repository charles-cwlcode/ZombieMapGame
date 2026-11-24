using System;
using System.Collections.Generic;
using ZombieMap.Core.Buildings;

namespace ZombieMap.Core.Geo
{
    /// <summary>WGS84 latitude/longitude in degrees.</summary>
    public struct LatLng
    {
        public double Lat;
        public double Lng;

        public LatLng(double lat, double lng)
        {
            Lat = lat;
            Lng = lng;
        }

        public bool IsValid =>
            !double.IsNaN(Lat) && !double.IsNaN(Lng) && !double.IsInfinity(Lat) && !double.IsInfinity(Lng) &&
            Lat >= -90 && Lat <= 90 && Lng >= -180 && Lng <= 180;

        public override string ToString() => $"{Lat:0.000000},{Lng:0.000000}";
    }

    /// <summary>
    /// Small-area geo maths. Good to well under a metre across a few kilometres, which is all the
    /// game needs (buildings, loot range, walking). Not for long distances.
    /// </summary>
    public static class GeoMath
    {
        public const double EarthRadiusMetres = 6371008.8;
        private const double DegToRad = Math.PI / 180.0;

        /// <summary>Great-circle distance in metres.</summary>
        public static double Distance(LatLng a, LatLng b)
        {
            double dLat = (b.Lat - a.Lat) * DegToRad;
            double dLng = (b.Lng - a.Lng) * DegToRad;
            double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(a.Lat * DegToRad) * Math.Cos(b.Lat * DegToRad) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
            return 2 * EarthRadiusMetres * Math.Asin(Math.Min(1, Math.Sqrt(h)));
        }

        /// <summary>Moves a point by metres east (x) and north (y).</summary>
        public static LatLng Offset(LatLng origin, double eastMetres, double northMetres)
        {
            double lat = origin.Lat + northMetres / EarthRadiusMetres / DegToRad;
            double lng = origin.Lng + eastMetres / (EarthRadiusMetres * Math.Cos(origin.Lat * DegToRad)) / DegToRad;
            return new LatLng(lat, lng);
        }

        /// <summary>Metres east (x) and north (y) of <paramref name="origin"/>.</summary>
        public static Vec2 ToLocal(LatLng origin, LatLng p)
        {
            double x = (p.Lng - origin.Lng) * DegToRad * EarthRadiusMetres * Math.Cos(origin.Lat * DegToRad);
            double y = (p.Lat - origin.Lat) * DegToRad * EarthRadiusMetres;
            return new Vec2(x, y);
        }

        /// <summary>Average of the points; fine as a local origin for a building outline.</summary>
        public static LatLng Centre(IReadOnlyList<LatLng> points)
        {
            double lat = 0, lng = 0;
            foreach (var p in points)
            {
                lat += p.Lat;
                lng += p.Lng;
            }
            return new LatLng(lat / points.Count, lng / points.Count);
        }
    }
}
