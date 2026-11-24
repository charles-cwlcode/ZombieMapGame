# ZombieMapGame

Location-based zombie survival: Pokémon Go-style map play, Project Zomboid-style realism.
Design notes and decisions: [docs/design.md](docs/design.md).

## Layout

| Folder | What |
|---|---|
| `client/` | Unity 6000.5.7f1 project (URP, Mapbox Unity SDK v3.1, Input System) |
| `shared/ZombieMap.Core/` | Shared rules, floor-plan generator and network protocol. A Unity package *and* a .NET library. |
| `server/ZombieMap.Server/` | ASP.NET Core WebSocket gateway + Orleans silo. `World/` = real map data (PostGIS + Overture import) |
| `server/ZombieMap.DevClient/` | Console smoke test that plays through the server |
| `tests/ZombieMap.Core.Tests/` | xUnit tests for the shared rules |
| `docs/` | Design notes |
| `docker-compose.yml` | Local PostGIS. The server starts it for you in Development. |

The Mapbox SDK is a local package next to this repo: `../mapbox-unity-sdk`
(see `client/Packages/manifest.json`).

## Run it

Needs the .NET 10 SDK, Unity 6000.5.7f1 and Docker Desktop.

**First time:** copy `server/ZombieMap.Server/appsettings.Local.example.json` to `appsettings.Local.json`
(git-ignored) and set your own test area (a town-sized box). On start, the server launches Docker
Desktop if needed, starts PostGIS (`docker compose`), and imports that area's buildings and places
from Overture Maps (about 2 minutes for a town, once). Later starts take seconds.

```powershell
# 1. Tests
dotnet test tests/ZombieMap.Core.Tests

# 2. Server (ws://localhost:5080/ws). Starts Docker/PostGIS and imports your map area on first run.
dotnet run --project server/ZombieMap.Server

# 3. In another terminal: smoke test against the running server
dotnet run --project server/ZombieMap.DevClient
```

Then open `client/` in Unity, open `Assets/_Game/Scenes/Dev.unity` (or run
**ZombieMap → Create Dev Scene**) and press Play. The dev HUD has five tabs:

- **Map**: the Mapbox map around you. In the editor the GPS is simulated: **WASD/arrows** walk
  (relative to the camera), **Shift** runs, **Ctrl** drives, **Q/E** or drag rotates, scroll zooms.
  The first time, you start at the server's dev start point (centre of its map area); after that,
  where you left off. Click a building: the server says which real building it is, its type and
  whether you're within loot range (80 m). **Search building** opens its generated floor plan.
- **Encounter**: the 8-lane tick prototype. A/D or arrow keys (or swipe) to turn. Hover an action
  to preview where zombies will be when it finishes. The shot clock sends Wait if you hesitate.
- **Building**: a building's generated floor plan. Click a room, press **Search** on a container (2–5 real
  seconds, timed by the server), then **Take** items or put them **To bag**. Hand-made test buildings
  can be made with the controls at the top.
- **Inventory**: pockets (8 kg) and the bag on your back, with weights. Move items between them, wear
  or take off a bag, drop items, or **Put** them into the container you last searched. **Wear** or
  **Take off** clothes.
- **Health**: your survivor's body in 3D, each zone green, amber, red or black. Callout boxes show
  each wound with buttons to bandage, disinfect or stitch it using what you carry. Blood, endurance,
  antibiotics and what you're wearing are along the edges, with food, water and rest, and **Sleep**
  (only at your safehouse). Drag or Q/E to turn; tap a zone for details. After death, **Next
  survivor** starts again and keeps the safehouse stash.

Everything on the body runs in real time, even while the game is closed: bandage bleeding wounds
before you stop. To get a safehouse, select a building on the **Map** tab and press **Make
safehouse**; the **Inventory** tab then has **Stash** buttons and the stash itself, plus **Eat** and
**Drink**.

**Mapbox token:** set it in Unity (Mapbox → Setup). It's saved in
`client/Assets/Resources/Mapbox/MapboxConfiguration.txt`, which is git-ignored.

### Testing on a phone
`localhost` on a phone is the phone. Run the server with the LAN profile
(`dotnet run --project server/ZombieMap.Server --launch-profile "LAN (phone testing)"`), allow it
through Windows Firewall, and set the URL in the dev HUD to `ws://<your PC's IP>:5080/ws`.

### Unity from the command line
Close the editor first (one Unity per project).

Map end to end in Play mode (opens a window): map loads, a tap finds a real building, floor plan, screenshot:
```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.5.7f1\Editor\Unity.exe" -projectPath client -executeMethod ZombieMap.Client.Editor.MapPlayModeTest.Run -screenshot map-test.png -logFile map-test.log
```

Unity ↔ server round trip (no graphics needed):
```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.5.7f1\Editor\Unity.exe" -batchmode -nographics -projectPath client -executeMethod ZombieMap.Client.Editor.DevTools.SmokeTestServer -logFile -
```

## Phase 0 status
- ✅ Repo, shared rules library (61 tests), Orleans server, WebSocket protocol
- ✅ Encounter prototype (8 lanes, ticks, wounds, healing, preview) running on the server
- ✅ Floor-plan generator (seeded by building ID, cached per building)
- ✅ Real building data: Overture → PostGIS with stable GERS IDs and building types, imported by the server itself
- ✅ Mapbox v3 map in Unity with keyboard GPS simulation; tap a real building → type, loot range, floor plan
- ⬜ Mapbox v3 map on a real phone (device GPS is wired up; needs a phone test)
- ⬜ CC5 character performance on a mid-range Android phone

## Phase 1 status
- ✅ Looting room by room, weight-based inventory with a worn bag
- ✅ Health and body: wounds that last in real time, treatment with your own supplies, clothing that
  blocks hits and wears, encounters that use your real body and bag, death and a new survivor
- ✅ Hunger, thirst and fatigue in real time; eating and drinking; a safehouse with a stash; sleeping there
- ✅ Death is a survivor line: the next survivor keeps the safehouse stash
- ✅ Everything is saved in PostgreSQL (`grain_state` table), so a server restart keeps players

## Mapbox SDK v3.1 needs a patch for Unity 6000.5
Stock v3.1 doesn't compile on Unity 6.5: `ElevatedTerrainStrategy.cs` uses `GetInstanceID()` and
`Physics.BakeMesh(int, …)`, which 6.5 turned into errors. The local clone in `../mapbox-unity-sdk`
is patched (uncommitted there): under `#if UNITY_6000_5_OR_NEWER`, `BakeColliderJob.MeshId` is an
`EntityId` set from `mesh.GetEntityId()`. A fresh clone of the SDK needs the same change.

The Mapbox samples aren't imported (v3.1's sample prefabs carry stale v2 components that spam
warnings). Import `LocationBasedGame` from the Package Manager if you need it as a reference.

## Not done yet (on purpose)
- No real sign-in: the client sends a device ID. Must be fixed before anything public.
- The client sends its own position with `building.at`. The server will track position itself.
- `floorPlan.get` doesn't check loot range yet (`building.at` does).
- The Building tab can still send hand-made footprints, for IDs the map data doesn't know.
  Real building IDs always use the server's own footprint.

## Map data licence
The Overture buildings and places used here are ODbL (from OpenStreetMap and Microsoft ML Buildings).
Before anything ships, the map must show "© OpenStreetMap contributors, Overture Maps Foundation".
Share-alike applies to the database itself if we ever publish it, not to the game.
