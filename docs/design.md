# ZombieMap: design notes

A location-based zombie survival game: Pokémon Go-style map play with Project Zomboid-style
realism in inventory, wounds and healing. Unity client, Microsoft Orleans server.

This file records decisions so far. Open questions are listed at the end.

## Core decisions

### One mode: everything happens on the map
No separate 3D scenes. Every interaction is a panel or overlay on top of the map, like Pokémon Go.
The Zomboid depth lives in **preparation and consequences**: what you carry, what condition it's
in, what's wrong with your body.

### Safety and real-world rules
- No encounters or looting above about 10 km/h (driving lockout, also anti-cheat).
- Buildings are looted from the street (about 40–80 m range). Never require entering private property.
- Never show another player's real base location.
- Hunger and thirst don't drain while you're offline. Offline you're sheltered at your safehouse,
  which feeds you from its stores. Online, needs drop with play time and distance walked.

### Map and buildings
- **Mapbox Unity SDK v3.1** (URP required) draws the map. The local package lives in
  `../mapbox-unity-sdk`.
- **Our own building data (phase 0 spike, done).** The server imports Overture Maps buildings
  and places into PostGIS (`server/ZombieMap.Server/World/`). Building IDs are Overture GERS IDs,
  which stay stable across releases. Mapbox's tiles are only the picture: the client never uses
  Mapbox building IDs. A tap sends a lat/lng, and the server finds our building there (6 m tolerance).
  - Import: DuckDB reads only the matching row groups of Overture's public GeoParquet on S3. It runs
    automatically on server start for any configured area not yet imported. The first test town (about
    5×6 km) has 17,137 buildings and 2,619 places, and takes about 2 minutes.
  - Licence: everything in that area is ODbL (OpenStreetMap 89%, Microsoft ML Buildings 11%).
    Show OSM/Overture attribution in the game.
  - **Building types.** About 80% of UK buildings have no type in the data, so we infer it, in this
    order. The rules re-run on every server start, so changing them needs no re-import.
    1. The building's own class (house, retail, office…).
    2. Size: an unlabelled building of 35–300 m² counts as a house.
    3. Places inside the footprint override both, by priority: station, then pharmacy, then
       shop/food, then office.
    - Stations need place confidence ≥ 0.8, and a station point may claim the nearest station-sized
      building within 40 m. Overture had a mis-placed station point in the test area that
      tagged a warehouse.
  - Loot range is checked on the server against the real wall (`ST_Distance` on geography), not the
    building's centre.
- **Interiors are generated**, seeded by building ID, and cached on the server
  (`FloorPlanGenerator` → `BuildingGrain`). Shown as a 2D floor plan with rooms you can tap.
- Normal buildings: **a separate loot copy for each player**. Special buildings: shared, contested state.

### Looting room by room (phase 1: first cut done)
- Tap a building in range → floor-plan panel → rooms → containers.
- **Done:**
  - **Searching takes real seconds:** 2–5 s by container type (`LootRules`). The server times it:
    the first `loot.search` starts the clock, and asking again before the time is up returns
    "still searching".
  - **Loot range:** every search, take and put checks that the player is within loot range of the
    real building's wall.
  - **Per-player copies:** loot is generated the first time each container is searched, from a seed
    of player + building + container, so each player has their own copy (`LootGrain`). Taking and
    putting back change that copy, and it stays as you left it.
  - **Loot tables:** per container, overridden per room and building type. Pharmacy dispensaries are
    full of medicine, shop shelves are groceries, wardrobes have clothes and bags (`LootTables`).
- **Still to come:**
  - thorough searches (slower, find more);
  - noise that pulls zombies in;
  - zombies hiding in rooms;
  - locked doors and windows;
  - loot respawning after a while;
  - shared loot in special buildings.

### Inventory (phase 1: first cut done)
- **Pockets and hands hold 8 kg.** One bag can be worn on the back. A worn bag's contents count at
  (1 − reduction): a hiking bag's are 60% lighter. A bag carried in the hands counts in full.
- **Heavy load:** carrying over 15 kg counts as a heavy load (penalties come with endurance).
- **Containers:** furniture has a capacity too, so you can put things back.
- **Item state:** count (stacks of small items), condition (weapons and clothing), uses left
  (disinfectant, painkillers, lighter), and a fresh-until day (food). Dropped items are destroyed
  for now; later they land on the ground.
- **New players start with:** 2 rags, a bandage, a bottle of water and a chocolate bar.
- **Item list:** about 60 items, kept in code (`ItemCatalog`). Their IDs are saved in
  inventories, so never rename one.

### Encounters: 8 lanes and ticks (phase 0 prototype done)
- You're in the middle. Zombies come in on 8 lanes, across rings 3 (far) → 2 → 1 (melee) → 0 (on you).
- Swipe to turn. Facing is always "up". The front 3 lanes are clear, side lanes show silhouettes,
  behind you there are only sound cues.
- Facing a zombie in reach shows hit chances per zone (head / torso / legs), like Fallout's VATS.
- **Time moves only when you act.** Each action costs ticks; then zombies get those ticks.
  If you hesitate past the **shot clock** (about 2 s, shorter when exhausted), a Wait is sent.

| Action | Ticks |
|---|---|
| Turn 45° | 0 (uses shot-clock time) |
| Attack, push, stomp, wait | 1 |
| Patch heal (stop bleeding, no disinfecting) | 2 |
| Flee (needs an open lane) | 2 |
| Full heal (disinfect + clean bandage) | 4 |
| Splint, stitch, remove glass | not in combat |

- Exhausted (endurance below 20) adds 1 tick to everything except turning.
- Zombie speed: runner 1 tick per ring, shambler 2, crawler 3. Leg hits turn zombies into crawlers.
- A successful push knocks a zombie down for 2 ticks, which is exactly one patch heal.
- Heals and fleeing are **channelled**: a zombie reaching you interrupts them and nothing is used up.
- At ring 0 a zombie rolls to hit a body part. Facing it lowers the chance. Two or more on you can
  drag you down.
- Wounds: scratch / laceration / bite with bleeding and hidden virus chance (bite 100%,
  laceration 25%, scratch 7%). The client never learns the virus result directly.
- Arena from real land use: field (open, all lanes), car park (cars block 2–3 lanes),
  street/alley (only N and S open), woods (sight limited to ring 1). Later: night, rain.
- Hold an action to preview where zombies will be when it finishes (`EncounterPreview`).
- All tuning numbers live in `shared/ZombieMap.Core/Runtime/Encounters/Rules.cs`.

### Equipment and health (planned, phase 1)
- Equipment: both hands, back (done), belt and holster attachments, layered clothing.
- Items have state: condition, freshness, partial uses, wet or dry.
- 17 body zones. Clothing has per-zone coverage, bite and scratch protection, insulation and
  condition. Attacks check layers from the outside in; layers can tear.
  The Character_Gen_POC layered clothing (Underwear/Base/Mid/Outer/Overcoat) is the visual side of this.
- Health screen: front and back body diagram. Bandages get dirty over time.
  Antibiotics cure wound infection; nothing cures the zombie virus.

### Special buildings, events, bases (phase 3+)
- Special buildings work like gyms with survival stakes: clear, hold, supply; control decays.
  Train station = remote raids on buildings along the line. Power plant = district power.
- Map events: helicopter crash, supply drop, convoy, outbreak, horde migration, blackout, real weather.
- Base: a safehouse in the MVP (stash, bed, offline food), a Clash of Clans-style base later.

### Art under the AI-only rule
- 2D AI images: item icons, room illustrations, 360° arena backdrops.
- 3D AI or CC5 models: zombies (one humanoid skeleton), avatar, special buildings.
- Code-generated: floor plans, map building extrusions.
- Clothing: a small set of CC5 garment shapes with AI textures (ties to protection data).
- Pipeline: Asset Forge (OpenAI image views → Meshy → CC5).

## Architecture

```
client/  Unity 6000.5 (URP) ──WebSocket JSON──▶ server/ZombieMap.Server (ASP.NET Core + Orleans)
   │                                                 │
   └──────── both compile shared/ZombieMap.Core ─────┘   (rules, generator, protocol)
```

- **shared/ZombieMap.Core** is plain C# 9 / netstandard2.1 with no Unity references. Unity uses it
  as a local package (`com.zombiemap.core`). .NET builds it through `shared/ZombieMap.Core.csproj`.
- **The server owns the game.** Encounters run in `EncounterGrain`. The client sends actions and
  gets back events plus what it's allowed to see.
- Map data lives in PostGIS, outside Orleans: `WorldStore` reads it, and `WorldStartup` starts
  Docker/PostGIS in Development and imports missing areas. `BuildingGrain` loads its footprint from
  `WorldStore` by ID, so a client can't change a real building's shape.
- Grains: `PlayerGrain` (player + active encounter), `EncounterGrain`, `BuildingGrain`.
  Later: `CellGrain` (map cells), `HordeGrain`, `LootSessionGrain`, `SafehouseGrain`.
- Core types cross grain calls as JSON (Newtonsoft codec), since Unity can't compile Orleans attributes.
- Protocol: one JSON envelope per frame, `{type, id, data}`. A reply uses the request's `id`.

## Phases

0. **Test the risky parts.** ✅ repo, shared rules, Orleans gateway, encounter + floor-plan
   prototypes, Unity ↔ server round trip. ✅ Real building data (Overture → PostGIS, stable IDs,
   building types). ✅ Mapbox v3 map in Unity with keyboard GPS simulation and tap-a-building.
   ⬜ Mapbox v3 on a phone (real GPS). ⬜ CC5 character performance on a mid-range phone.
1. **Vertical slice.** ✅ Walk the map, loot a house room by room, weight-based inventory.
   ⬜ Wounds and clothing check, healing with your own supplies, eating, sleeping at a safehouse, dying.
2. Zombies and hordes on the map, noise, infection, spoilage, crafting, real weather,
   the AI item pipeline.
3. Special buildings, events, factions, remote raids.
4. Full bases, survivors, offline horde defence.

## Open questions
- How harsh is death? (Permadeath vs a survivor line that keeps some progress.)
- Zombie virus timer: real time or play time?
- Encounter end: a fixed zombie budget, or keep going until you flee or clear it? (Gunshots add zombies.)
- Multiplayer: solo in a shared world, co-op raids, PvP?
- Platforms and minimum device.
