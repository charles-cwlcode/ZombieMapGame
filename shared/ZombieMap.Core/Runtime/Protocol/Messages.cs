using System.Collections.Generic;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Health;
using ZombieMap.Core.Items;
using ZombieMap.Core.Zombies;

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
        /// <summary>After dying: the next survivor in your line. Your safehouse and its stash are kept.</summary>
        public const string NewSurvivor = "player.newSurvivor";
        /// <summary>Eat or drink one of a stack (StackRequest).</summary>
        public const string HealthEat = "health.eat";
        /// <summary>Sleep at your safehouse (PositionRequest). Any other action wakes you.</summary>
        public const string Sleep = "body.sleep";
        public const string Wake = "body.wake";
        /// <summary>Make the building you're at your safehouse (SafehouseClaimRequest).</summary>
        public const string SafehouseClaim = "safehouse.claim";
        /// <summary>Move items between you and the safehouse stash (SafehouseItemRequest).</summary>
        public const string SafehouseStore = "safehouse.store";
        public const string SafehouseRetrieve = "safehouse.retrieve";
        /// <summary>Where you are now (PlayerMoveRequest). Replies with the zombies around you, and a fight if you walked into them.</summary>
        public const string PlayerMove = "player.move";

        // Server → client
        public const string Welcome = "welcome";
        public const string Pong = "pong";
        public const string FloorPlan = "floorPlan";
        public const string Building = "building";
        public const string Inventory = "inventory";
        public const string Loot = "loot";
        public const string Health = "health";
        public const string Nearby = "world.nearby";
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

    /// <summary>Phase 1 trusts the client's position for range checks, as with looting.</summary>
    public sealed class PositionRequest
    {
        public LatLng PlayerPosition;
    }

    /// <summary>Sent every few seconds (and after moving) while the map is open.</summary>
    public sealed class PlayerMoveRequest
    {
        public LatLng PlayerPosition;
        /// <summary>False: show zombies but never start a fight (dev, or while just looking around).</summary>
        public bool AllowFights = true;
    }

    public sealed class SafehouseClaimRequest
    {
        public string BuildingId;
        public LatLng PlayerPosition;
    }

    /// <summary>Store: from your inventory into the stash. Retrieve: from the stash into "main" or "back".</summary>
    public sealed class SafehouseItemRequest
    {
        public string StackId;
        /// <summary>0 = the whole stack.</summary>
        public int Count;
        public string To;
        public LatLng PlayerPosition;
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
        /// <summary>Null until you claim one.</summary>
        public SafehouseView Safehouse;
        /// <summary>Which survivor in your line this is (1 = the first).</summary>
        public int SurvivorNumber = 1;
    }

    /// <summary>Reply to player.move: the map cells and zombies around you.</summary>
    public sealed class NearbyMessage
    {
        /// <summary>Your cell and the two rings around it, with zombie counts (for a danger overlay).</summary>
        public List<ZombieCellView> Cells = new List<ZombieCellView>();
        /// <summary>Zombies in your cell and the ring around it, where they are right now.</summary>
        public List<ZombieMarker> Zombies = new List<ZombieMarker>();
        /// <summary>Your cell's H3 index.</summary>
        public string Cell;
        /// <summary>Set when you walked into zombies: the fight that just started.</summary>
        public EncounterStateMessage Encounter;
        /// <summary>Server clock (unix ms) the positions are for.</summary>
        public long TimeMs;
    }

    /// <summary>Your safehouse: a real building, with a stash that outlives each survivor.</summary>
    public sealed class SafehouseView
    {
        public string BuildingId;
        public BuildingKind Kind;
        public ItemContainer Stash;
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
        public const int Version = 6;
    }
}
