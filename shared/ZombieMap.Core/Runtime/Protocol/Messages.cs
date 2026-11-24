using ZombieMap.Core.Buildings;
using ZombieMap.Core.Encounters;

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
        public const string StartEncounter = "encounter.start";
        public const string EncounterAct = "encounter.act";

        // Server → client
        public const string Welcome = "welcome";
        public const string Pong = "pong";
        public const string FloorPlan = "floorPlan";
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
        public BuildingFootprint Footprint;
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
    }

    public sealed class FloorPlanMessage
    {
        public FloorPlan Plan;
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
        public const int Version = 1;
    }
}
