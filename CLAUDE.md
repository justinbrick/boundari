# Boundari

A territorial strategy game in the style of OpenFront, built in **Godot 4.7.2 (mono / C#)**, .NET 8.
Real-world maps (Europe is the default), bots (tribes) and nations, single player for now; multiplayer later.

## Golden rules

- **Copy OpenFront's mechanics and numbers exactly, but write our own code.** OpenFront: github.com/openfrontio/OpenFrontIO (AGPL). Research its source before building a feature (`curl -sL https://raw.githubusercontent.com/openfrontio/OpenFrontIO/main/<path>`; key places: `packages/engine/src/execution/`, `packages/engine/src/configuration/`, `src/client/hud/`, `src/client/render/gl/`, `rs.json`).
- **Scale OpenFront's numbers:** UI sizes in CSS px x ~1.5 for our screen; anything measured in map tiles goes through `game.Rules` (ScaledRules: `D()` distances, `A()` tile counts), because our Europe is finer than OpenFront's.
- **The simulation is deterministic:** plain C# in `game/sim/` with no Godot types; players change it only through intents (`Game.Submit`); use `Core.Rng` (never System.Random); sort HashSet contents before iterating in sim code.
- **The sim runs on a worker thread:** any main-thread code that reads or changes the game must call `SyncSim()` first.
- **The UI is the user's:** they design scenes (`boundari.tscn`, `RadialMenu.tscn`) and some GDScript in the Godot editor. Look nodes up tolerantly (`GetNodeOrNull` / `FindChild` / `get_node_or_null`) and never crash when a node is missing or hidden; they move, hide and delete things while designing.
- **Display names** differ from code names: Port = Harbor, City = Barracks, Factory = Depot, SAM = Flak, MOAB = Mortar, Fat Man = Missile, Tsar Bomba = Atomic Bomb. Use the display names when talking to the user.

## Working with the user

- Explain things plainly (they're not a programmer by trade); say when something was only checked by tests or screenshots rather than played.
- Commit your own work with short messages (`feat: ...`, `fix: ...`); the user pushes. Never commit their uncommitted scene edits (`boundari.tscn`) unless they asked for that change. Put risky changes in their own commit.
- Ask before anything that downloads data (e.g. building a map at a new land-cover size streams ESA WorldCover).
- Their design notes live in `C:\Users\Joe\Desktop\GIANTFOLDER\Notepad++\Boundari_Gameplay.txt`: read it any time, implement only what they approve.

## Layout

- `src/Boundari/` the Godot project. `game/World.cs` (+ `WorldDiplomacy.cs`) is the scene root: sim thread, input, keyboard shortcuts, CLI test flags.
- `game/sim/` the game rules (`Game` is a partial class split by topic: attacks, clusters/pockets, alliances, embargoes, chat, nukes, events...), AI (`NationAi`, `BotAi`), `Balance`/`*Rules` numbers, `ScaledRules`.
- `game/view/` drawing and HUD: `MapView` + `map.gdshader` (terrain, territory, borders), `TerritoryLabels`, `MapOverlay*` (buildings, roads, ships, bombs, pop-ups), `GameHud`, `PlayerBar`, `PlayerPanels`, `NotificationBar`, `OutgoingAttacks`, `RadialChat`, `DevTools`.
- `game/map/` map data (`.bmap` files in `maps/`), `tools/MapBuilder` builds them.
- `src/Boundari.Tests/` xunit tests for the sim.

## Commands (from `src/Boundari` unless noted)

- Build: `dotnet build Boundari.csproj`
- Tests: `dotnet test` in `src/Boundari.Tests`
- Headless match benchmark: `BOUNDARI_BENCH=1 BOUNDARI_MINUTES=12 dotnet test -c Release --filter EuropeBotMatch` (in `src/Boundari.Tests`)
- Godot (console build): `"C:\Users\Joe\Desktop\GoDot Ten\Godot_v4.7.2-stable_mono_win64_console.exe" --path . -- <flags>`
  - Screenshot: `--map=europe --autoplay --ticks=N --zoom=Z --screenshot=PATH` plus extras such as `--focus=x,y`, `--hover=leader`, `--showcase[=rails|nukes:N]`, `--aim=moab|fatman`, `--incoming`, `--ally-requests=N`, `--right-click=requester-accept|requester-ally-menu|requester-embargo|requester-target|requester-chat-T|R|B|L|requester-heart`, `--start=now|mid|ask`, `--dev=ABBR=i,...`
  - Performance: `--map=europe --autoplay --bench=SECONDS`
- New PNG/SVG assets need an import: Godot `--headless --path . --import`.

## Shell notes (Windows)

- Bash heredocs containing apostrophes break: write edit scripts to a file and run `python -I script.py`.
- Paths for Godot arguments: convert with `cygpath -m`.
