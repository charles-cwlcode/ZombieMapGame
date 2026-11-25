using Xunit;
using ZombieMap.Core.Geo;

namespace ZombieMap.Core.Tests;

public class SunTests
{
    private static readonly LatLng London = new(51.5, -0.12);

    [Fact]
    public void MidsummerNoonInLondonIsHighInTheSouth()
    {
        var sun = SunMath.At(new DateTime(2026, 6, 21, 12, 2, 0, DateTimeKind.Utc), London);
        Assert.InRange(sun.ElevationDegrees, 60.5, 63);
        Assert.InRange(sun.AzimuthDegrees, 170, 190);
    }

    [Fact]
    public void MidwinterNoonInLondonIsLow()
    {
        var sun = SunMath.At(new DateTime(2026, 12, 21, 12, 0, 0, DateTimeKind.Utc), London);
        Assert.InRange(sun.ElevationDegrees, 13.5, 16);
    }

    [Fact]
    public void MidnightIsDarkAndMorningIsRising()
    {
        Assert.True(SunMath.At(new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc), London).ElevationDegrees < -20);
        var morning = SunMath.At(new DateTime(2026, 3, 20, 8, 0, 0, DateTimeKind.Utc), London);
        Assert.True(morning.Morning);
        Assert.InRange(morning.AzimuthDegrees, 95, 140); // in the east-south-east
        Assert.False(SunMath.At(new DateTime(2026, 3, 20, 16, 0, 0, DateTimeKind.Utc), London).Morning);
    }

    [Fact]
    public void EquinoxSunsetInLondonIsAroundSixInTheWest()
    {
        var set = SunMath.NextChange(new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc), London);
        Assert.NotNull(set);
        Assert.InRange(set!.Value.TimeOfDay.TotalHours, 18.0, 18.4); // ~18:12 UTC
        Assert.InRange(SunMath.At(set.Value, London).AzimuthDegrees, 260, 285);

        var rise = SunMath.NextChange(set.Value.AddMinutes(30), London);
        Assert.NotNull(rise);
        Assert.InRange(rise!.Value.TimeOfDay.TotalHours, 5.9, 6.3); // ~06:03 UTC next day
    }
}
