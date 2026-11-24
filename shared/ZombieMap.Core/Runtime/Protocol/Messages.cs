using System.Collections.Generic;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Health;
using ZombieMap.Core.Items;

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
        public const string InventoryGet = "inventory.get";
        public const string InventoryMove = "inventory.move";
        public const string InventoryEquip = "inventory.equip";
        public const string InventoryUnequip = "inventory.unequip";
        public const string InventoryDrop = "inventory.drop";
        public const string LootSearch = "loot.search";
        public const string LootTake = "loot.take";
        public const string LootPut = "loot.put";
        public const string HealthGet = "health.get";
        public const string HealthTreat = "health.treat";
        public const string HealthMedicine = "health.medicine";
        public const string ClothingWear = "clothing.wear";
        public const string ClothingTakeOff = "clothing.takeOff";
        /// <summary>Dev: after dying, start again with a fresh body and the starter kit.</summary>
        public const string NewSurvivor = "player.newSurvivor";

        // Server → client
        public const string Welcome = "welcome";
        public const string Pong = "pong";
        public const string FloorPlan = "floorPlan";
        public const string Building = "building";
        public const string Inventory = "inventory";
        public const string Loot = "loot";
        public const string Health = "health";
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

    /// <summary>
    /// Search one container in a building. The first call starts the search and says how long it
    /// takes; call again when the time is up to get the contents. Searched containers open at once.
    /// </summary>
    public sealed class LootSearchRequest
    {
        public string BuildingId;
        public string ContainerId;
        /// <summary>Phase 1 trusts the client's position for the loot-range check.</summary>
        public LatLng PlayerPosition;
    }

    /// <summary>Take items from a searched container into "main" (pockets) or "back" (worn bag).</summary>
    public sealed class LootTakeRequest
    {
        public string BuildingId;
        public string ContainerId;
        public string StackId;
        /// <summary>0 = the whole stack.</summary>
        public int Count;
        public string To = PlayerInventory.MainId;
        public LatLng PlayerPosition;
    }

    /// <summary>Put items from your inventory into a container you've searched.</summary>
    public sealed class LootPutRequest
    {
        public string BuildingId;
        public string ContainerId;
        public string StackId;
        public int Count;
        public LatLng PlayerPosition;
    }

    /// <summary>Move items between pockets ("main") and worn bag ("back").</summary>
    public sealed class InventoryMoveRequest
    {
        public string StackId;
        public int Count;
        public string To;
    }

    public sealed class InventoryEquipRequest
    {
        public string StackId;
    }

    /// <summary>Phase 1: dropped items are gone. Later they land on the ground where you are.</summary>
    public sealed class HealthTreatRequest
    {
        public int WoundId;
        public TreatAction Action;
    }

    /// <summary>Take one dose from a stack (antibiotics), or wear / take off a piece of clothing.</summary>
    public sealed class StackRequest
    {
        public string StackId;
    }

    public sealed class InventoryDropRequest
    {
        public string StackId;
        public int Count;
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

    public sealed class InventoryMessage
    {
        public PlayerInventory Inventory;
    }

    /// <summary>The body (as the client may see it) and the inventory, after any change.</summary>
    public sealed class HealthMessage
    {
        public BodyView Body;
        public PlayerInventory Inventory;
        /// <summary>Server clock (unix seconds), for showing how dirty bandages are.</summary>
        public long Now;
    }

    public enum SearchStatus { Searching, Done }

    /// <summary>Reply to loot.search/take/put: the container's state and your inventory after the change.</summary>
    public sealed class LootMessage
    {
        public string BuildingId;
        public string ContainerId;
        public SearchStatus Status;
        /// <summary>While searching: real seconds until it's done.</summary>
        public double SecondsLeft;
        /// <summary>When done: what's in it.</summary>
        public ItemContainer Contents;
        public PlayerInventory Inventory;
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
        public const int Version = 4;
    }
}
