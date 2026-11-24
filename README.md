# ZombieMapGame

Location-based zombie survival: Pokémon Go-style map play, Project Zomboid-style realism.
Design notes and decisions: [docs/design.md](docs/design.md).

## Layout

| Folder | What |
|---|---|
| `client/` | Unity 6000.5.7f1 project (URP, Mapbox Unity SDK v3.1, Input System) |
| `shared/ZombieMap.Core/` | Shared rules, floor-plan generator and network protocol. A Unity package *and* a .NET library. |
| `server/ZombieMap.Server/` | ASP.NET Core WebSocket gateway + Orleans silo |
| `server/ZombieMap.DevClient/` | Console smoke test that plays through the server |
| `tests/ZombieMap.Core.Tests/` | xUnit tests for the shared rules |
| `docs/` | Design notes |

The Mapbox SDK is a local package next to this repo: `../mapbox-unity-sdk`
(see `client/Packages/manifest.json`).

## Run it

Needs the .NET 10 SDK and Unity 6000.5.7f1.

```powershell
# 1. Tests
dotnet test tests/ZombieMap.Core.Tests

# 2. Server (ws://localhost:5080/ws)
dotnet run --project server/ZombieMap.Server

# 3. In another terminal: smoke test against the running server
dotnet run --project server/ZombieMap.DevClient
```

Then open `client/` in Unity, open `Assets/_Game/Scenes/Dev.unity` (or run
**ZombieMap → Create Dev Scene**) and press Play. The dev HUD has two tabs:

- **Encounter**: the 8-lane tick prototype. A/D or arrow keys (or swipe) to turn. Hover an action
  to preview where zombies will be when it finishes. The shot clock sends Wait if you hesitate.
- **Building**: asks the server for a generated floor plan and draws it. Click rooms to see containers.

**Mapbox token:** set it in Unity (Mapbox → Setup). It's saved in
`client/Assets/Resources/Mapbox/MapboxConfiguration.txt`, which is git-ignored.

### Testing on a phone
`localhost` on a phone is the phone. Run the server with the LAN profile
(`dotnet run --project server/ZombieMap.Server --launch-profile "LAN (phone testing)"`), allow it
through Windows Firewall, and set the URL in the dev HUD to `ws://<your PC's IP>:5080/ws`.

### Unity from the command line
```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.5.7f1\Editor\Unity.exe" -batchmode -nographics -projectPath client -executeMethod ZombieMap.Client.Editor.DevTools.SmokeTestServer -logFile -
```

## Phase 0 status
- ✅ Repo, shared rules library (36 tests), Orleans server, WebSocket protocol
- ✅ Encounter prototype (8 lanes, ticks, wounds, healing, preview) running on the server
- ✅ Floor-plan generator (seeded by building ID, cached per building)
- ⬜ Mapbox v3 map on a real phone
- ⬜ Real building data: Overture/OSM → PostGIS, stable IDs, building types
- ⬜ CC5 character performance on a mid-range Android phone

## Mapbox SDK v3.1 needs a patch for Unity 6000.5
Stock v3.1 doesn't compile on Unity 6.5: `ElevatedTerrainStrategy.cs` uses `GetInstanceID()` and
`Physics.BakeMesh(int, …)`, which 6.5 turned into errors. The local clone in `../mapbox-unity-sdk`
is patched (uncommitted there): under `#if UNITY_6000_5_OR_NEWER`, `BakeColliderJob.MeshId` is an
`EntityId` set from `mesh.GetEntityId()`. A fresh clone of the SDK needs the same change.

The Mapbox samples aren't imported (v3.1's sample prefabs carry stale v2 components that spam
warnings). Import `LocationBasedGame` from the Package Manager if you need it as a reference.

## Not done yet (on purpose)
- No real sign-in: the client sends a device ID. Must be fixed before anything public.
- In-memory storage only: restarting the server forgets everything.
- The client sends building footprints. The server will load them from its own data instead.
