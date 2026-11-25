using System;

namespace ZombieMap.Core.Geo
{
    /// <summary>Where the sun is in the sky, from a position and a moment (UTC).</summary>
    public readonly struct SunPosition
    {
        /// <summary>Degrees above the horizon (negative below it).</summary>
        public readonly double ElevationDegrees;

        /// <summary>Compass bearing of the sun, degrees clockwise from north.</summary>
        public readonly double AzimuthDegrees;

        /// <summary>True before solar noon (the sun is rising), false after.</summary>
        public readonly bool Morning;

        public SunPosition(double elevation, double azimuth, bool morning)
        {
            ElevationDegrees = elevation;
            AzimuthDegrees = azimuth;
            Morning = morning;
        }
    }

    /// <summary>
    /// The sun's position, accurate to well under a degree (the low-precision formulas from the
    /// Astronomical Almanac), and when it next rises or sets. Deterministic: every player in a town
    /// gets the same dusk at the same moment.
    /// </summary>
    public static class SunMath
    {
        /// <summary>Sunrise and sunset are when the sun's top edge crosses the horizon, refraction included.</summary>
        public const double HorizonDegrees = -0.833;

        private const double Deg = Math.PI / 180.0;

        public static SunPosition At(DateTime utc, LatLng where)
        {
            if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();
            double d = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;

            double g = Normalise(357.529 + 0.98560028 * d) * Deg;                 // mean anomaly
            double q = Normalise(280.459 + 0.98564736 * d);                       // mean longitude
            double lambda = (q + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * Deg; // ecliptic longitude
            double obliquity = (23.439 - 0.00000036 * d) * Deg;

            double ra = Math.Atan2(Math.Cos(obliquity) * Math.Sin(lambda), Math.Cos(lambda));
            double dec = Math.Asin(Math.Sin(obliquity) * Math.Sin(lambda));

            double gmstHours = 18.697374558 + 24.06570982441908 * d;
            double lst = Normalise(gmstHours * 15.0 + where.Lng) * Deg;
            double hourAngle = lst - ra;
            hourAngle = Math.Atan2(Math.Sin(hourAngle), Math.Cos(hourAngle)); // -π..π, negative before noon

            double lat = where.Lat * Deg;
            double elevation = Math.Asin(Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * Math.Cos(hourAngle));
            double azimuth = Math.Atan2(-Math.Sin(hourAngle), Math.Tan(dec) * Math.Cos(lat) - Math.Sin(lat) * Math.Cos(hourAngle));

            return new SunPosition(elevation / Deg, Normalise(azimuth / Deg), hourAngle < 0);
        }

        /// <summary>
        /// The next sunset (if the sun is up) or sunrise (if it's down) within <paramref name="searchHours"/>,
        /// to the minute. Null in polar day or night.
        /// </summary>
        public static DateTime? NextChange(DateTime utc, LatLng where, double searchHours = 26)
        {
            if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();
            bool up = At(utc, where).ElevationDegrees > HorizonDegrees;
            // Coarse 10-minute steps, then refine to the minute.
            for (int m = 10; m <= searchHours * 60; m += 10)
            {
                var t = utc.AddMinutes(m);
                if (At(t, where).ElevationDegrees > HorizonDegrees == up) continue;
                for (int k = m - 9; k <= m; k++)
                {
                    var f = utc.AddMinutes(k);
                    if (At(f, where).ElevationDegrees > HorizonDegrees != up) return f;
                }
                return t;
            }
            return null;
        }

        private static double Normalise(double degrees)
        {
            degrees %= 360.0;
            return degrees < 0 ? degrees + 360.0 : degrees;
        }
    }
}
