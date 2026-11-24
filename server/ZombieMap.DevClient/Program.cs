// Smoke test for the server: hello → floor plan (twice, must be cached) → real map building
// near the server's dev start point → play one encounter with a dumb bot → check error paths.
// Exit code 0 = everything worked.
//
//   dotnet run --project server/ZombieMap.DevClient -- [ws://localhost:5080/ws]

using System.Net.WebSockets;
using System.Text;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Items;
using ZombieMap.Core.Protocol;

string url = args.Length > 0 ? args[0] : "ws://localhost:5080/ws";
using var socket = new ClientWebSocket();
await socket.ConnectAsync(new Uri(url), CancellationToken.None);
Console.WriteLine($"Connected to {url}");
int nextId = 1;
int failures = 0;

// 1. Hello
var welcome = (await Request(MessageTypes.Hello, new HelloRequest
{
    PlayerId = "devclient-" + Guid.NewGuid().ToString("N")[..8],
    DisplayName = "Dev Client",
})).DataAs<WelcomeMessage>();
Console.WriteLine($"Welcome {welcome.DisplayName} ({welcome.PlayerId})");

// 2. Floor plan for an L-shaped house, twice.
var footprint = new BuildingFootprint
{
    BuildingId = "dev-house-" + Guid.NewGuid().ToString("N")[..6],
    Kind = BuildingKind.House,
    Points = new List<Vec2> { new(0, 0), new(14, 0), new(14, 6), new(6, 6), new(6, 12), new(0, 12) },
};
var plan = (await Request(MessageTypes.GetFloorPlan, new GetFloorPlanRequest { Footprint = footprint })).DataAs<FloorPlanMessage>().Plan;
Console.WriteLine($"\nFloor plan for {plan.BuildingId}: {plan.Rooms.Count} rooms, {plan.Doors.Count} doors");
foreach (var room in plan.Rooms)
    Console.WriteLine($"  [{room.Id}] {room.Type,-11} {room.Width:0.0}×{room.Height:0.0} m  {string.Join(", ", room.Containers.Select(c => c.Kind))}");

var again = (await Request(MessageTypes.GetFloorPlan, new GetFloorPlanRequest { Footprint = footprint })).DataAs<FloorPlanMessage>().Plan;
Check(ProtocolJson.Serialize(plan) == ProtocolJson.Serialize(again), "second floor-plan request returns the same plan");

// 3. Real map data: find a building near the server's dev start point, then generate its interior.
if (welcome.DevStart is LatLng start)
{
    BuildingInfo? found = null;
    LatLng probe = start;
    // Walk outward in a square spiral (15 m steps) until a tap hits a building.
    for (int ring = 0; ring <= 20 && found == null; ring++)
    for (int dx = -ring; dx <= ring && found == null; dx++)
    for (int dy = -ring; dy <= ring && found == null; dy++)
    {
        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring) continue;
        probe = GeoMath.Offset(start, dx * 15, dy * 15);
        found = (await Request(MessageTypes.BuildingAt, new BuildingAtRequest { Point = probe, PlayerPosition = probe }))
            .DataAs<BuildingMessage>().Building;
    }
    Check(found != null, "building.at finds a real building near the dev start point");
    if (found != null)
    {
        Console.WriteLine($"\nReal building {found.BuildingId}: {found.Kind} {found.Name}, {found.Outline.Count}-point outline");
        // The probe may hit up to the tap tolerance (6 m) outside the wall.
        Check(found.InRange && found.DistanceMetres <= 7, $"player at the tap point is in loot range ({found.DistanceMetres} m)");

        var far = (await Request(MessageTypes.BuildingAt, new BuildingAtRequest { Point = probe, PlayerPosition = GeoMath.Offset(probe, 500, 0) }))
            .DataAs<BuildingMessage>().Building;
        Check(far != null && !far.InRange && far.DistanceMetres > 400, "player 500 m away is out of loot range");

        var real = (await Request(MessageTypes.GetFloorPlan, new GetFloorPlanRequest { BuildingId = found.BuildingId })).DataAs<FloorPlanMessage>().Plan;
        Console.WriteLine($"  Interior: {real.Rooms.Count} rooms ({string.Join(", ", real.Rooms.Select(r => r.Type))}), {real.Width:0}×{real.Height:0} m");
        Check(real.Rooms.Count > 0 && real.BuildingId == found.BuildingId, "floor plan by real building ID");

        // A client can't reshape a real building by sending its own footprint.
        var spoofed = (await Request(MessageTypes.GetFloorPlan, new GetFloorPlanRequest { Footprint = new BuildingFootprint
        {
            BuildingId = found.BuildingId, Kind = BuildingKind.Station,
            Points = new List<Vec2> { new(0, 0), new(100, 0), new(100, 100), new(0, 100) },
        } })).DataAs<FloorPlanMessage>().Plan;
        Check(ProtocolJson.Serialize(spoofed) == ProtocolJson.Serialize(real), "client footprint can't override a real building");

        await LootChecks(found.BuildingId, real, probe);
    }

    var unknown = await Request(MessageTypes.GetFloorPlan, new GetFloorPlanRequest { BuildingId = "no-such-building-" + Guid.NewGuid().ToString("N")[..6] }, allowError: true);
    Check(unknown.Type == MessageTypes.Error && unknown.DataAs<ErrorMessage>().Code == "unknown_building", "unknown building ID is rejected");
}
else
{
    Console.WriteLine("\nSKIP: server sent no dev start point (no World:Areas configured), so no real-building checks.");
}

// 4. Encounter with a simple bot that only uses what the client can see.
var state = (await Request(MessageTypes.StartEncounter, new StartEncounterRequest { Arena = ArenaKind.Field, ZombieCount = 4 }))
    .DataAs<EncounterStateMessage>();
Console.WriteLine($"\nEncounter {state.EncounterId} started: {state.Result.View.ZombiesRemaining} zombies");
var view = state.Result.View;

for (int step = 0; step < 120 && view.Status == EncounterStatus.Ongoing; step++)
{
    var action = ChooseAction(view);
    var env = await Request(MessageTypes.EncounterAct, new EncounterActRequest { EncounterId = state.EncounterId, Action = action });
    var result = env.Type == MessageTypes.EncounterState ? env.DataAs<EncounterStateMessage>().Result : null;
    if (result == null || !result.Accepted)
    {
        // Bot picked something not allowed right now; hesitate instead.
        Console.WriteLine($"  ({action.Kind} rejected: {result?.Error ?? env.Data?.ToString()})");
        env = await Request(MessageTypes.EncounterAct, new EncounterActRequest { EncounterId = state.EncounterId, Action = EncounterAction.Of(EncounterActionKind.Wait) });
        result = env.DataAs<EncounterStateMessage>().Result;
    }
    view = result.View;
    foreach (var ev in result.Events.Where(e => e.Text != null))
        Console.WriteLine($"  t{ev.Tick,-3} {ev.Text}");
}
Console.WriteLine($"Encounter ended: {view.Status}{(view.DeathCause != null ? $" ({view.DeathCause})" : "")}, tick {view.Tick}, blood {view.Blood}, wounds {view.Wounds.Count}");
Check(view.Status != EncounterStatus.Ongoing, "encounter reached an end state");

// 5. Error path: acting on an encounter that isn't active.
var err = await Request(MessageTypes.EncounterAct, new EncounterActRequest { EncounterId = state.EncounterId, Action = EncounterAction.Of(EncounterActionKind.Wait) });
Check(err.Type == MessageTypes.Error && err.DataAs<ErrorMessage>().Code == "no_such_encounter", "finished encounter rejects further actions");

await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
Console.WriteLine(failures == 0 ? "\nSMOKE TEST PASSED" : $"\nSMOKE TEST FAILED ({failures})");
return failures == 0 ? 0 : 1;

// ------------------------------------------------------------------ helpers

EncounterAction ChooseAction(EncounterView v)
{
    bool bleeding = v.Wounds.Any(w => w.Bleeding);
    var reachable = v.Zombies.Where(z => !z.Silhouette && z.Ring <= 1).OrderBy(z => z.Ring).ToList();
    int? soonest = v.Zombies.Select(EncounterPreview.TicksUntilContact).Where(t => t.HasValue).Min();
    bool hasPatch = v.Supplies.Rags > 0 || v.Supplies.Bandages > 0;

    // Bleeding and nothing will reach us within 2 ticks → patch.
    if (bleeding && hasPatch && (soonest == null || soonest > 2) && !v.Sounds.Any(s => s.Close))
        return EncounterAction.Of(EncounterActionKind.PatchHeal);

    var target = reachable.FirstOrDefault(z => z.Lane == v.Facing);
    if (target != null)
        return target.KnockedDownTicks > 0 ? EncounterAction.Of(EncounterActionKind.Stomp) : EncounterAction.Attack(HitZone.Torso);

    // Turn toward the closest threat we can see, or a close sound behind us.
    int? lane = reachable.FirstOrDefault()?.Lane
                ?? v.Sounds.FirstOrDefault(s => s.Close)?.Lane
                ?? v.Zombies.Where(z => z.Silhouette && z.Ring <= 1).Select(z => (int?)z.Lane).FirstOrDefault();
    if (lane != null && lane != v.Facing)
    {
        int steps = Lanes.Rotate(lane.Value - v.Facing, 0);
        return EncounterAction.Turn(steps <= 4 ? steps : steps - 8);
    }
    return EncounterAction.Of(EncounterActionKind.Wait);
}

async Task<Envelope> Request(string type, object data, bool allowError = false)
{
    int id = nextId++;
    await socket.SendAsync(Encoding.UTF8.GetBytes(ProtocolJson.Write(type, data, id)), WebSocketMessageType.Text, true, CancellationToken.None);
    var buffer = new byte[64 * 1024];
    using var ms = new MemoryStream();
    WebSocketReceiveResult r;
    do
    {
        r = await socket.ReceiveAsync(buffer, CancellationToken.None);
        ms.Write(buffer, 0, r.Count);
    } while (!r.EndOfMessage);
    var envelope = ProtocolJson.Read(Encoding.UTF8.GetString(ms.ToArray()));
    if (envelope.Id != id) throw new InvalidOperationException($"Reply id {envelope.Id} != request id {id}");
    if (envelope.Type == MessageTypes.Error && type != MessageTypes.EncounterAct && !allowError)
        throw new InvalidOperationException($"Server error: {envelope.Data}");
    return envelope;
}

void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
    if (!ok) failures++;
}

// Search every container in a real building, then take an item and put it back.
async Task LootChecks(string buildingId, FloorPlan plan, LatLng here)
{
    var inv = (await Request(MessageTypes.InventoryGet, new { })).DataAs<InventoryMessage>().Inventory;
    Console.WriteLine($"\nStarting inventory: {string.Join(", ", inv.Main.Items.Select(s => $"{s.Count}× {s.Def().Name}"))} ({inv.CarriedWeight:0.##} kg)");
    Check(inv.CountOf(d => d.Medical == MedicalUse.Rag) == 2 && inv.Back == null, "new player starts with the starter kit");

    var containers = plan.Rooms.SelectMany(r => r.Containers).ToList();
    var far = await Request(MessageTypes.LootSearch, new LootSearchRequest
    {
        BuildingId = buildingId, ContainerId = containers[0].Id, PlayerPosition = GeoMath.Offset(here, 500, 0),
    }, allowError: true);
    Check(far.Type == MessageTypes.Error && far.DataAs<ErrorMessage>().Code == "too_far", "can't search a building from 500 m away");

    var early = await Request(MessageTypes.LootTake, new LootTakeRequest
    {
        BuildingId = buildingId, ContainerId = containers[0].Id, StackId = "x", PlayerPosition = here,
    }, allowError: true);
    Check(early.Type == MessageTypes.Error && early.DataAs<ErrorMessage>().Code == "not_searched", "can't take from a container before searching it");

    // Start every search, then finish them once the longest is done.
    double longest = 0;
    foreach (var c in containers)
    {
        var started = (await Request(MessageTypes.LootSearch, new LootSearchRequest { BuildingId = buildingId, ContainerId = c.Id, PlayerPosition = here }))
            .DataAs<LootMessage>();
        if (c == containers[0])
            Check(started.Status == SearchStatus.Searching && started.SecondsLeft >= 2, $"searching takes time ({started.SecondsLeft} s for a {c.Kind})");
        longest = Math.Max(longest, started.SecondsLeft);
    }
    var tooSoon = (await Request(MessageTypes.LootSearch, new LootSearchRequest { BuildingId = buildingId, ContainerId = containers[0].Id, PlayerPosition = here }))
        .DataAs<LootMessage>();
    Check(tooSoon.Status == SearchStatus.Searching, "asking again straight away is still searching");

    await Task.Delay(TimeSpan.FromSeconds(longest + 0.2));
    LootMessage? withItems = null;
    int itemsFound = 0;
    foreach (var c in containers)
    {
        var done = (await Request(MessageTypes.LootSearch, new LootSearchRequest { BuildingId = buildingId, ContainerId = c.Id, PlayerPosition = here }))
            .DataAs<LootMessage>();
        if (done.Status != SearchStatus.Done) { Check(false, $"{c.Id} finished searching"); continue; }
        itemsFound += done.Contents.Items.Count;
        Console.WriteLine($"  {done.Contents.Name,-16} {string.Join(", ", done.Contents.Items.Select(s => $"{s.Count}× {s.Def().Name}"))}");
        if (withItems == null && done.Contents.Items.Count > 0) withItems = done;
    }
    Check(withItems != null, $"the building has loot ({itemsFound} stacks in {containers.Count} containers)");
    if (withItems == null) return;

    var again = (await Request(MessageTypes.LootSearch, new LootSearchRequest { BuildingId = buildingId, ContainerId = withItems.ContainerId, PlayerPosition = here }))
        .DataAs<LootMessage>();
    Check(ProtocolJson.Serialize(again.Contents) == ProtocolJson.Serialize(withItems.Contents), "searched containers stay the same");

    var item = withItems.Contents.Items.OrderBy(s => InventoryRules.Weight(s)).First();
    var took = (await Request(MessageTypes.LootTake, new LootTakeRequest
    {
        BuildingId = buildingId, ContainerId = withItems.ContainerId, StackId = item.Id, PlayerPosition = here,
    })).DataAs<LootMessage>();
    var mine = took.Inventory.Main.Items.FirstOrDefault(s => s.ItemId == item.ItemId && s.Id.StartsWith('i'));
    Check(took.Contents.Find(item.Id) == null && mine != null, $"took the {item.Def().Name.ToLowerInvariant()} into my pockets");

    if (mine != null)
    {
        var put = (await Request(MessageTypes.LootPut, new LootPutRequest
        {
            BuildingId = buildingId, ContainerId = withItems.ContainerId, StackId = mine.Id, PlayerPosition = here,
        })).DataAs<LootMessage>();
        Check(put.Contents.Items.Any(s => s.ItemId == item.ItemId) && put.Inventory.Main.Find(mine.Id) == null, "put it back");
    }
}
