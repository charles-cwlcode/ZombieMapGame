using System.Collections.Generic;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Geo;

namespace ZombieMap.Core.Protocol
{
    /// <summary>
    /// Message type names. Every WebSocket frame is one JSON Envelope:
    ///   { "type": "encounter.act", "id": 7, "data": { ... } }
    /// A reply carries the same "id" as the request it answers. Pushes from the server have id 0.
    /// </summary>
    public static class MessageTypes
    {
        // Client → server
        public const string Hello = "hello";
        public const string Ping = "ping";
        public const string GetFloorPlan = "floorPlan.get";
        public const string BuildingAt = "building.at";
        public const string StartEncounter = "encounter.start";
        public const string EncounterAct = "encounter.act";

        // Server → client
        public const string Welcome = "welcome";
        public const string Pong = "pong";
        public const string FloorPlan = "floorPlan";
        public const string Building = "building";
        public const string EncounterState = "encounter.state";
        public const string Error = "error";
    }

    // ---------- client → server ----------

    public sealed class HelloRequest
    {
        /// <summary>Phase 0: a device-generated ID. Real auth comes later.</summary>
        public string PlayerId;
        public string DisplayName;
        public int ProtocolVersion = ProtocolInfo.Version;
    }

    public sealed class GetFloorPlanRequest
    {
        /// <summary>A real building's ID (from building.at). The server loads its footprint itself.</summary>
        public string BuildingId;

        /// <summary>Dev only: a hand-made footprint, for IDs the server's map data doesn't know.</summary>
        public BuildingFootprint Footprint;
    }

    /// <summary>"Which building is here?" Sent when the player taps the map.</summary>
    public sealed class BuildingAtRequest
    {
        /// <summary>Where the player tapped.</summary>
        public LatLng Point;

        /// <summary>
        /// Where the player is. Phase 0 trusts the client; later the server tracks position from
        /// location updates and ignores this.
        /// </summary>
        public LatLng PlayerPosition;
    }

    public sealed class StartEncounterRequest
    {
        public ArenaKind Arena = ArenaKind.Field;
        /// <summary>Phase 0 debug: how many zombies. The server clamps it.</summary>
        public int ZombieCount = 4;
    }

    public sealed class EncounterActRequest
    {
        public string EncounterId;
        public EncounterAction Action;
    }

    // ---------- server → client ----------

    public sealed class WelcomeMessage
    {
        public string PlayerId;
        public string DisplayName;
        public long ServerTimeUnixMs;
        public string ActiveEncounterId;
        public int ProtocolVersion = ProtocolInfo.Version;

        /// <summary>Dev servers only: a starting point inside the loaded map area, for GPS simulation.</summary>
        public LatLng? DevStart;
    }

    public sealed class FloorPlanMessage
    {
        public FloorPlan Plan;
    }

    /// <summary>Reply to building.at. Building is null if there's no building at that spot.</summary>
    public sealed class BuildingMessage
    {
        public BuildingInfo Building;
    }

    public sealed class BuildingInfo
    {
        /// <summary>Stable ID (Overture GERS ID). Use it for floorPlan.get.</summary>
        public string BuildingId;
        public BuildingKind Kind;
        public string Name;

        /// <summary>Outer wall, as a closed-or-open ring of points.</summary>
        public List<LatLng> Outline = new List<LatLng>();

        /// <summary>Distance from the player to the nearest wall, in metres (0 if inside).</summary>
        public double DistanceMetres;
        public double LootRangeMetres;
        public bool InRange;
    }

    public sealed class EncounterStateMessage
    {
        public string EncounterId;
        public EncounterResult Result;
    }

    public sealed class ErrorMessage
    {
        public string Code;
        public string Message;
    }

    public static class ProtocolInfo
    {
        public const int Version = 2;
    }
}
