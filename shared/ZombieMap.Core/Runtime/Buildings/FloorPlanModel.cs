using System.Collections.Generic;

namespace ZombieMap.Core.Buildings
{
    public struct Vec2
    {
        public double X;
        public double Y;

        public Vec2(double x, double y) { X = x; Y = y; }

        public override string ToString() => $"({X:0.##}, {Y:0.##})";
    }

    /// <summary>What the map data says the building is. Drives room templates and loot.</summary>
    public enum BuildingKind { Unknown, House, Shop, Pharmacy, Office, Station }

    public enum RoomType
    {
        Room, Hallway, LivingRoom, Kitchen, Bathroom, Bedroom, Storage,
        SalesFloor, Dispensary, Office, Restroom, Concourse,
    }

    public enum ContainerKind
    {
        Fridge, Cupboard, Counter, Cabinet, Wardrobe, Drawers, Shelf, Closet,
        Crate, Desk, FilingCabinet, MedicineCabinet, Locker, Bin,
    }

    /// <summary>
    /// A building outline in metres on a local flat plane (x east, y north), e.g. projected
    /// from lat/lon around the building. Phase 0: the client sends it. Later the server loads
    /// it from its own building database (Overture/OSM) by BuildingId.
    /// </summary>
    public sealed class BuildingFootprint
    {
        /// <summary>Stable ID (e.g. Overture GERS ID). Seeds the layout.</summary>
        public string BuildingId;
        public BuildingKind Kind;
        public List<Vec2> Points = new List<Vec2>();
        public int Levels = 1;
    }

    public sealed class Container
    {
        /// <summary>Unique inside the building: "roomId.index". Loot state is keyed by (BuildingId, Id).</summary>
        public string Id;
        public ContainerKind Kind;
    }

    public sealed class Room
    {
        public int Id;
        public RoomType Type;
        /// <summary>Rectangle in plan space (metres, see FloorPlan for the transform).</summary>
        public double X, Y, Width, Height;
        public List<Container> Containers = new List<Container>();

        public double Area => Width * Height;
        public double CenterX => X + Width * 0.5;
        public double CenterY => Y + Height * 0.5;
    }

    public sealed class Door
    {
        public const int Outside = -1;

        public int RoomA;
        /// <summary>Other room's Id, or Outside.</summary>
        public int RoomB;
        public double X, Y;
    }

    /// <summary>
    /// Generated interior. Plan space = footprint rotated so its longest wall is horizontal,
    /// then shifted so the bounding box starts at (0,0):
    ///   plan = rotate(point - Pivot, -Rotation) - Offset
    /// </summary>
    public sealed class FloorPlan
    {
        public string BuildingId;
        public BuildingKind Kind;
        public int GeneratorVersion;
        public double PivotX, PivotY, Rotation, OffsetX, OffsetY;
        public double Width, Height;
        public List<Vec2> Outline = new List<Vec2>();
        public int EntranceRoomId;
        public List<Room> Rooms = new List<Room>();
        public List<Door> Doors = new List<Door>();
    }
}
