# MapBuilder

Builds Boundari's real-world maps (`src/Boundari/maps/*.bmap`) from free geographic data.
Players never need this: the built `.bmap` files ship with the game.

## Use

1. Get the source data once (about 40 MB, saved to `tools/data/`, not committed):
   `powershell -File tools/download-data.ps1`
2. Set up the Python tools once (used to read land-cover data):
   `python -m venv tools/.venv` then `tools/.venv/Scripts/python.exe -m pip install rasterio`
3. Build a map from the repo root:
   `dotnet run -c Release --project tools/MapBuilder -- europe` (or `world`)
   (no map name = build all of them)

Maps are defined in `Program.cs` (`Specs`): the longitude/latitude box, width in tiles,
Natural Earth detail level, the smallest island/lake to keep, and optionally a play area.

A play area (`PlayArea.cs`, Europe's is `EuropePlayArea` in `Program.cs`) is where the game can
be played. Its land is real countries (Natural Earth admin 1 provinces/regions, so country borders
too): the countries listed are in whole; `SplitCountries` (Russia, Kazakhstan) go region by region,
a region being in when most of its land is inside a rough outline (hand-placed points the edge
curves through, and stretches that follow a real river by name). The build prints the regions
the outline cuts through; add a name or ISO code to `AlwaysIn` / `AlwaysOut` to change one, or
to `CutByOutline` to cut it along the outline. A region that runs off the map is cut along the
outline (so there's no straight cut at the map's edge), and small playable patches cut off from
the rest are dropped. Land outside becomes `Outside` terrain: dark grey in the game like the
world around Europe, and never claimable. Water is all playable. The map also stores the edge as
a smooth field, which the game draws it from.

The world drawn around Europe (its land dark grey) has its own build:
`dotnet run -c Release --project tools/MapBuilder -- worldbackdrop` (~1-2 minutes) writes the whole
world at Europe's detail as 1024x1024-tile pieces to `src/Boundari/maps/world/*.wchunk`
(greyscale PNGs of the coast field), which the game streams in where the camera is zoomed in
(`view/WorldBackdrop.cs`), and `src/Boundari/maps/world_overview.bmap` (the same at a sixth of
the detail, for zoomed out). Rebuild it whenever Europe's box or tile size changes. A game
export must include `*.wchunk` files (and `*.bmap`) as non-resource files.

To try out a change to the outline quickly, without land cover (nothing is downloaded):
`dotnet run -c Release --project tools/MapBuilder -- europe --preview` writes a small picture to
`tools/data/preview_europe.ppm` (green/blue = playable, grey = outside).

## How it works

1. Land and lake polygons are filled into the tile grid (a tile is land if its centre is).
2. For every tile, the signed distance to the real coastline is measured. Near the shore this
   is exact, from the vector outlines, so the game's shader draws the true coast shape,
   smoothed, at any zoom.
3. Elevation is averaged from terrain tiles over each tile's area and mapped to plains
   (< 350 m), highlands (< 2000 m) and mountains.
4. Tiny islands and lakes are removed with the same code the random generator uses.
5. Rivers (Natural Earth river lines, up to `RiverMaxRank`) are cut into the map as real,
   connected water tiles, `RiverWidth` tiles wide for the biggest and never narrower than
   1.6 tiles, so boats can always get through.
6. Maps with `LandCoverScale` get real land cover (farmland, meadow, forest, rock, snow,
   small lakes and rivers) on a grid finer than the game tiles. `tools/landcover/worldcover.py`
   reads only the small built-in overviews of ESA WorldCover over the internet (about
   25 s for Europe) and caches the result in `tools/data/worldcover/`. Towns are filled in
   with the surrounding land on purpose, and so are ponds under ~60 km² and thin river slivers.

## Credits (must be shown in the game's credits)

- Coastlines and lakes: **Natural Earth** (naturalearthdata.com), public domain.
- Land cover: **ESA WorldCover 2021** (CC BY 4.0). "© ESA WorldCover project 2021 /
  Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium."
- Elevation: **Terrain Tiles** on AWS Open Data, made by Mapzen, with data from
  SRTM, GMTED2010, ETOPO1 and others. See
  https://github.com/tilezen/joerd/blob/master/docs/attribution.md
