using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Items;
using ZombieMap.Core.Protocol;
using ZombieMap.Server.Grains;
using ZombieMap.Server.World;

namespace ZombieMap.Server.Gateway;

/// <summary>
/// One WebSocket connection. Reads JSON envelopes, routes them to grains, writes the reply
/// with the same request id. Phase 0 is request/response only; server pushes (zombies moving
/// on the map, events) will go out through the same socket later.
/// </summary>
public sealed partial class GameSocketHandler(
    IGrainFactory grains,
    WorldStore world,
    IOptions<WorldOptions> worldOptions,
    IHostEnvironment hostEnvironment,
    ILogger<GameSocketHandler> log)
{
    private const int MaxMessageBytes = 64 * 1024;
    private const int MaxFootprintPoints = 200;
    private const double MaxFootprintExtentMetres = 500;

    [GeneratedRegex("^[A-Za-z0-9_-]{4,64}$")]
    private static partial Regex PlayerIdPattern();

    public async Task RunAsync(WebSocket socket, CancellationToken ct)
    {
        string? playerId = null;
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, ct);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                    return;
                }
                message.Write(buffer, 0, received.Count);
                if (message.Length > MaxMessageBytes)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", ct);
                    return;
                }
            } while (!received.EndOfMessage);

            if (received.MessageType != WebSocketMessageType.Text) continue;

            string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            string reply;
            int requestId = 0;
            try
            {
                var envelope = ProtocolJson.Read(text);
                requestId = envelope.Id;
                (reply, playerId) = await HandleAsync(envelope, playerId);
            }
            catch (JsonException)
            {
                reply = Error(requestId, "bad_message", "Couldn't read that message.");
            }
            catch (GameRuleException ex)
            {
                reply = Error(requestId, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error handling message for player {PlayerId}", playerId);
                reply = Error(requestId, "server_error", "Something went wrong on the server.");
            }

            await socket.SendAsync(Encoding.UTF8.GetBytes(reply), WebSocketMessageType.Text, true, ct);
        }
    }

    private async Task<(string reply, string? playerId)> HandleAsync(Envelope env, string? playerId)
    {
        switch (env.Type)
        {
            case MessageTypes.Ping:
                return (ProtocolJson.Write(MessageTypes.Pong, new { }, env.Id), playerId);

            case MessageTypes.Hello:
            {
                var hello = env.DataAs<HelloRequest>() ?? new HelloRequest();
                if (hello.ProtocolVersion != ProtocolInfo.Version)
                    return (Error(env.Id, "protocol_mismatch", $"Server speaks protocol {ProtocolInfo.Version}. Update the game."), playerId);
                // Phase 0: trust the device ID. Real sign-in comes before anything public.
                if (hello.PlayerId == null || !PlayerIdPattern().IsMatch(hello.PlayerId))
                    return (Error(env.Id, "bad_player_id", "Player ID must be 4–64 letters, digits, - or _."), playerId);

                var welcome = await grains.GetGrain<IPlayerGrain>(hello.PlayerId).Hello(hello.DisplayName);
                if (hostEnvironment.IsDevelopment()) welcome.DevStart = DevStart();
                log.LogInformation("Player {PlayerId} connected", hello.PlayerId);
                return (ProtocolJson.Write(MessageTypes.Welcome, welcome, env.Id), hello.PlayerId);
            }
        }

        if (playerId == null)
            return (Error(env.Id, "not_signed_in", "Send hello first."), playerId);

        switch (env.Type)
        {
            case MessageTypes.GetFloorPlan:
            {
                var request = env.DataAs<GetFloorPlanRequest>() ?? new GetFloorPlanRequest();
                var footprint = request.Footprint;
                string? buildingId = footprint?.BuildingId ?? request.BuildingId;
                if (footprint != null)
                {
                    string? problem = ValidateFootprint(footprint);
                    if (problem != null) return (Error(env.Id, "bad_footprint", problem), playerId);
                }
                else if (string.IsNullOrWhiteSpace(buildingId) || buildingId.Length > 128)
                {
                    return (Error(env.Id, "bad_building_id", "Send a building ID (1–128 characters)."), playerId);
                }
                // TODO(phase 1): check the player is within loot range of a real building before showing its rooms.

                var plan = await grains.GetGrain<IBuildingGrain>(buildingId!).GetFloorPlan(footprint);
                return (ProtocolJson.Write(MessageTypes.FloorPlan, new FloorPlanMessage { Plan = plan }, env.Id), playerId);
            }

            case MessageTypes.BuildingAt:
            {
                var request = env.DataAs<BuildingAtRequest>();
                if (request == null || !request.Point.IsValid || !request.PlayerPosition.IsValid)
                    return (Error(env.Id, "bad_position", "Send a valid tap point and player position."), playerId);

                var building = await world.FindBuildingAtAsync(request.Point, request.PlayerPosition);
                return (ProtocolJson.Write(MessageTypes.Building, new BuildingMessage { Building = building }, env.Id), playerId);
            }

            case MessageTypes.StartEncounter:
            {
                var request = env.DataAs<StartEncounterRequest>() ?? new StartEncounterRequest();
                var state = await grains.GetGrain<IPlayerGrain>(playerId).StartEncounter(request);
                return (ProtocolJson.Write(MessageTypes.EncounterState, state, env.Id), playerId);
            }

            case MessageTypes.EncounterAct:
            {
                var request = env.DataAs<EncounterActRequest>();
                if (request?.Action == null || string.IsNullOrEmpty(request.EncounterId))
                    return (Error(env.Id, "bad_action", "Missing encounter ID or action."), playerId);
                var state = await grains.GetGrain<IPlayerGrain>(playerId).Act(request.EncounterId, request.Action);
                return (ProtocolJson.Write(MessageTypes.EncounterState, state, env.Id), playerId);
            }

            // ---------- inventory ----------

            case MessageTypes.InventoryGet:
                return (Inventory(env.Id, await grains.GetGrain<IPlayerGrain>(playerId).GetInventory()), playerId);

            case MessageTypes.InventoryMove:
            {
                var r = env.DataAs<InventoryMoveRequest>();
                if (r == null || !ValidId(r.StackId) || !ValidId(r.To)) return (Error(env.Id, "bad_request", "Missing item or target."), playerId);
                return (Inventory(env.Id, await grains.GetGrain<IPlayerGrain>(playerId).Move(r.StackId, r.Count, r.To)), playerId);
            }

            case MessageTypes.InventoryEquip:
            {
                var r = env.DataAs<InventoryEquipRequest>();
                if (r == null || !ValidId(r.StackId)) return (Error(env.Id, "bad_request", "Missing item."), playerId);
                return (Inventory(env.Id, await grains.GetGrain<IPlayerGrain>(playerId).Equip(r.StackId)), playerId);
            }

            case MessageTypes.InventoryUnequip:
                return (Inventory(env.Id, await grains.GetGrain<IPlayerGrain>(playerId).Unequip()), playerId);

            case MessageTypes.InventoryDrop:
            {
                var r = env.DataAs<InventoryDropRequest>();
                if (r == null || !ValidId(r.StackId)) return (Error(env.Id, "bad_request", "Missing item."), playerId);
                return (Inventory(env.Id, await grains.GetGrain<IPlayerGrain>(playerId).Drop(r.StackId, r.Count)), playerId);
            }

            // ---------- health and clothing ----------

            case MessageTypes.HealthGet:
                return (ProtocolJson.Write(MessageTypes.Health, await grains.GetGrain<IPlayerGrain>(playerId).GetHealth(), env.Id), playerId);

            case MessageTypes.HealthTreat:
            {
                var r = env.DataAs<HealthTreatRequest>();
                if (r == null || r.WoundId <= 0) return (Error(env.Id, "bad_request", "Which wound?"), playerId);
                return (ProtocolJson.Write(MessageTypes.Health, await grains.GetGrain<IPlayerGrain>(playerId).Treat(r.WoundId, r.Action), env.Id), playerId);
            }

            case MessageTypes.HealthMedicine:
            case MessageTypes.ClothingWear:
            case MessageTypes.ClothingTakeOff:
            {
                var r = env.DataAs<StackRequest>();
                if (r == null || !ValidId(r.StackId)) return (Error(env.Id, "bad_request", "Missing item."), playerId);
                var player = grains.GetGrain<IPlayerGrain>(playerId);
                var reply = env.Type == MessageTypes.HealthMedicine ? await player.TakeMedicine(r.StackId)
                    : env.Type == MessageTypes.ClothingWear ? await player.Wear(r.StackId)
                    : await player.TakeOff(r.StackId);
                return (ProtocolJson.Write(MessageTypes.Health, reply, env.Id), playerId);
            }

            case MessageTypes.NewSurvivor:
                return (ProtocolJson.Write(MessageTypes.Health, await grains.GetGrain<IPlayerGrain>(playerId).NewSurvivor(), env.Id), playerId);

            // ---------- food, sleep, safehouse ----------

            case MessageTypes.HealthEat:
            {
                var r = env.DataAs<StackRequest>();
                if (r == null || !ValidId(r.StackId)) return (Error(env.Id, "bad_request", "Missing item."), playerId);
                return (ProtocolJson.Write(MessageTypes.Health, await grains.GetGrain<IPlayerGrain>(playerId).EatOrDrink(r.StackId), env.Id), playerId);
            }

            case MessageTypes.Sleep:
            {
                var r = env.DataAs<PositionRequest>();
                var player = grains.GetGrain<IPlayerGrain>(playerId);
                await RequireAtSafehouse(player, r?.PlayerPosition ?? default);
                return (ProtocolJson.Write(MessageTypes.Health, await player.Sleep(), env.Id), playerId);
            }

            case MessageTypes.Wake:
                return (ProtocolJson.Write(MessageTypes.Health, await grains.GetGrain<IPlayerGrain>(playerId).Wake(), env.Id), playerId);

            case MessageTypes.SafehouseClaim:
            {
                var r = env.DataAs<SafehouseClaimRequest>();
                if (r == null || !ValidBuildingId(r.BuildingId)) return (Error(env.Id, "bad_request", "Which building?"), playerId);
                await RequireInLootRange(r.BuildingId, r.PlayerPosition);
                var kind = await SafehouseKind(r.BuildingId);
                return (ProtocolJson.Write(MessageTypes.Health, await grains.GetGrain<IPlayerGrain>(playerId).ClaimSafehouse(r.BuildingId, kind), env.Id), playerId);
            }

            case MessageTypes.SafehouseStore:
            case MessageTypes.SafehouseRetrieve:
            {
                var r = env.DataAs<SafehouseItemRequest>();
                if (r == null || !ValidId(r.StackId)) return (Error(env.Id, "bad_request", "Missing item."), playerId);
                var player = grains.GetGrain<IPlayerGrain>(playerId);
                await RequireAtSafehouse(player, r.PlayerPosition);
                HealthMessage reply;
                if (env.Type == MessageTypes.SafehouseStore) reply = await player.StoreInSafehouse(r.StackId, r.Count);
                else if (!ValidId(r.To)) return (Error(env.Id, "bad_request", "Missing target."), playerId);
                else reply = await player.RetrieveFromSafehouse(r.StackId, r.Count, r.To);
                return (ProtocolJson.Write(MessageTypes.Health, reply, env.Id), playerId);
            }

            // ---------- looting (loot range is checked here, on the server) ----------

            case MessageTypes.LootSearch:
            {
                var r = env.DataAs<LootSearchRequest>();
                if (r == null || !ValidBuildingId(r.BuildingId) || !ValidId(r.ContainerId)) return (Error(env.Id, "bad_request", "Missing building or container."), playerId);
                await RequireInLootRange(r.BuildingId, r.PlayerPosition);
                var reply = await grains.GetGrain<IPlayerGrain>(playerId).Search(r.BuildingId, r.ContainerId);
                return (ProtocolJson.Write(MessageTypes.Loot, reply, env.Id), playerId);
            }

            case MessageTypes.LootTake:
            {
                var r = env.DataAs<LootTakeRequest>();
                if (r == null || !ValidBuildingId(r.BuildingId) || !ValidId(r.ContainerId) || !ValidId(r.StackId) || !ValidId(r.To))
                    return (Error(env.Id, "bad_request", "Missing building, container, item or target."), playerId);
                await RequireInLootRange(r.BuildingId, r.PlayerPosition);
                var reply = await grains.GetGrain<IPlayerGrain>(playerId).Take(r.BuildingId, r.ContainerId, r.StackId, r.Count, r.To);
                return (ProtocolJson.Write(MessageTypes.Loot, reply, env.Id), playerId);
            }

            case MessageTypes.LootPut:
            {
                var r = env.DataAs<LootPutRequest>();
                if (r == null || !ValidBuildingId(r.BuildingId) || !ValidId(r.ContainerId) || !ValidId(r.StackId))
                    return (Error(env.Id, "bad_request", "Missing building, container or item."), playerId);
                await RequireInLootRange(r.BuildingId, r.PlayerPosition);
                var reply = await grains.GetGrain<IPlayerGrain>(playerId).Put(r.BuildingId, r.ContainerId, r.StackId, r.Count);
                return (ProtocolJson.Write(MessageTypes.Loot, reply, env.Id), playerId);
            }

            default:
                return (Error(env.Id, "unknown_type", $"Unknown message type '{env.Type}'."), playerId);
        }
    }

    private static string? ValidateFootprint(BuildingFootprint? f)
    {
        if (f == null) return "Missing footprint.";
        if (string.IsNullOrWhiteSpace(f.BuildingId) || f.BuildingId.Length > 128) return "Building ID must be 1–128 characters.";
        if (f.Points == null || f.Points.Count < 3 || f.Points.Count > MaxFootprintPoints) return $"Footprint needs 3–{MaxFootprintPoints} points.";
        foreach (var p in f.Points)
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return "Footprint has invalid coordinates.";
        double w = f.Points.Max(p => p.X) - f.Points.Min(p => p.X);
        double h = f.Points.Max(p => p.Y) - f.Points.Min(p => p.Y);
        if (w > MaxFootprintExtentMetres || h > MaxFootprintExtentMetres) return "Footprint is too large.";
        if (w < 2 || h < 2) return "Footprint is too small.";
        return null;
    }

    /// <summary>
    /// Throws a game error unless the player is within loot range of a real building. Hand-made dev
    /// buildings (IDs the map data doesn't know) skip the check, on dev servers only.
    /// </summary>
    private async Task RequireInLootRange(string buildingId, LatLng position)
    {
        bool dev = hostEnvironment.IsDevelopment();
        double? distance;
        try
        {
            if (!position.IsValid) throw new GameRuleException("bad_position", "Send your position.");
            distance = await world.DistanceToBuildingAsync(buildingId, position);
        }
        catch (GameRuleException ex) when (dev && ex.Code is "world_offline" or "world_loading" or "bad_position")
        {
            return; // dev building while map data is down
        }

        if (distance == null)
        {
            if (dev) return;
            throw new GameRuleException("unknown_building", "There's no building with that ID.");
        }
        if (distance > world.LootRangeMetres)
            throw new GameRuleException("too_far", $"You're {distance:0} m away. Get within {world.LootRangeMetres:0} m.");
    }

    /// <summary>Sleeping and the stash need you to be at your safehouse (within loot range of it).</summary>
    private async Task RequireAtSafehouse(IPlayerGrain player, LatLng position)
    {
        var id = await player.SafehouseId() ?? throw new GameRuleException("no_safehouse", "You don't have a safehouse yet. Claim a building first.");
        try
        {
            await RequireInLootRange(id, position);
        }
        catch (GameRuleException ex) when (ex.Code == "too_far")
        {
            throw new GameRuleException("too_far", "You need to be at your safehouse. " + ex.Message);
        }
    }

    /// <summary>The building type from the map data. Dev buildings the map doesn't know count as houses.</summary>
    private async Task<BuildingKind> SafehouseKind(string buildingId)
    {
        try
        {
            var footprint = await world.GetFootprintAsync(buildingId);
            if (footprint != null) return footprint.Kind;
        }
        catch (GameRuleException) when (hostEnvironment.IsDevelopment()) { }
        if (!hostEnvironment.IsDevelopment()) throw new GameRuleException("unknown_building", "There's no building with that ID.");
        return BuildingKind.House;
    }

    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 64;

    /// <summary>'|' separates player and building in loot grain keys.</summary>
    private static bool ValidBuildingId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Contains('|');

    private static string Inventory(int id, PlayerInventory inventory) =>
        ProtocolJson.Write(MessageTypes.Inventory, new InventoryMessage { Inventory = inventory }, id);

    /// <summary>Centre of the first configured map area, so GPS simulation starts somewhere with buildings.</summary>
    private LatLng? DevStart()
    {
        var area = worldOptions.Value.Areas.FirstOrDefault(a => a.IsValid);
        return area == null ? null : new LatLng((area.South + area.North) / 2, (area.West + area.East) / 2);
    }

    private static string Error(int id, string code, string message) =>
        ProtocolJson.Write(MessageTypes.Error, new ErrorMessage { Code = code, Message = message }, id);
}
