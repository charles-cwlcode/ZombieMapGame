using Xunit;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Core.Tests;

public class GeoTests
{
    private static readonly LatLng Origin = new(51.5, -0.12);

    [Fact]
    public void OneDegreeOfLatitudeIsAbout111Km()
    {
        double d = GeoMath.Distance(new LatLng(51, 0), new LatLng(52, 0));
        Assert.InRange(d, 111_000, 111_400);
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(0, 100)]
    [InlineData(-250, 80)]
    [InlineData(30, -45)]
    public void OffsetMovesTheRightDistance(double east, double north)
    {
        var moved = GeoMath.Offset(Origin, east, north);
        Assert.Equal(Math.Sqrt(east * east + north * north), GeoMath.Distance(Origin, moved), 1);
    }

    [Fact]
    public void ToLocalUndoesOffset()
    {
        var moved = GeoMath.Offset(Origin, 37.5, -12.25);
        var local = GeoMath.ToLocal(Origin, moved);
        Assert.Equal(37.5, local.X, 2);
        Assert.Equal(-12.25, local.Y, 2);
    }

    [Fact]
    public void LocalFootprintFromLatLngIsTheRightSize()
    {
        // A 12 m × 9 m box made from lat/lng corners → generator footprint in metres.
        var ring = new List<LatLng>
        {
            Origin, GeoMath.Offset(Origin, 12, 0), GeoMath.Offset(Origin, 12, 9), GeoMath.Offset(Origin, 0, 9),
        };
        var centre = GeoMath.Centre(ring);
        var points = ring.Select(p => GeoMath.ToLocal(centre, p)).ToList();
        Assert.Equal(12, points.Max(p => p.X) - points.Min(p => p.X), 1);
        Assert.Equal(9, points.Max(p => p.Y) - points.Min(p => p.Y), 1);

        var plan = FloorPlanGenerator.Generate(new BuildingFootprint { BuildingId = "geo-test", Kind = BuildingKind.House, Points = points });
        Assert.NotEmpty(plan.Rooms);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(91, 0)]
    [InlineData(0, 181)]
    [InlineData(double.PositiveInfinity, 0)]
    public void InvalidPositionsAreRejected(double lat, double lng) => Assert.False(new LatLng(lat, lng).IsValid);

    [Fact]
    public void BuildingMessagesRoundTripThroughProtocolJson()
    {
        var msg = new BuildingMessage
        {
            Building = new BuildingInfo
            {
                BuildingId = "08b1-abc", Kind = BuildingKind.Pharmacy, Name = "Chemist",
                Outline = { Origin, GeoMath.Offset(Origin, 10, 0), GeoMath.Offset(Origin, 10, 10) },
                DistanceMetres = 12.5, LootRangeMetres = 80, InRange = true,
            },
        };
        var env = ProtocolJson.Read(ProtocolJson.Write(MessageTypes.Building, msg, 3));
        var back = env.DataAs<BuildingMessage>().Building;
        Assert.Equal("08b1-abc", back.BuildingId);
        Assert.Equal(BuildingKind.Pharmacy, back.Kind);
        Assert.Equal(3, back.Outline.Count);
        Assert.Equal(Origin.Lat, back.Outline[0].Lat, 9);

        var welcome = ProtocolJson.Read(ProtocolJson.Write(MessageTypes.Welcome, new WelcomeMessage { PlayerId = "p", DevStart = Origin }, 1))
            .DataAs<WelcomeMessage>();
        Assert.Equal(Origin.Lng, welcome.DevStart!.Value.Lng, 9);
        Assert.Null(ProtocolJson.Read(ProtocolJson.Write(MessageTypes.Welcome, new WelcomeMessage { PlayerId = "p" }, 1))
            .DataAs<WelcomeMessage>().DevStart);
    }
}
