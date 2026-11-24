using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Geo;
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

    /// <summary>Centre of the first configured map area, so GPS simulation starts somewhere with buildings.</summary>
    private LatLng? DevStart()
    {
        var area = worldOptions.Value.Areas.FirstOrDefault(a => a.IsValid);
        return area == null ? null : new LatLng((area.South + area.North) / 2, (area.West + area.East) / 2);
    }

    private static string Error(int id, string code, string message) =>
        ProtocolJson.Write(MessageTypes.Error, new ErrorMessage { Code = code, Message = message }, id);
}
