# MapBuilder

Builds Boundari's real-world maps (`src/Boundari/maps/*.bmap`) from free geographic data.
Players never need this: the built `.bmap` files ship with the game.

## Use

1. Get the source data once (about 60 MB, saved to `tools/data/`, not committed):
   `powershell -File tools/download-data.ps1`
2. Set up the Python tools once (used to read land-cover data):
   `python -m venv tools/.venv` then `tools/.venv/Scripts/python.exe -m pip install rasterio`
3. Build a map from the repo root:
   `dotnet run -c Release --project tools/MapBuilder -- europe` (or `world`, `ardennes`)
   (no map name = build all of them)

Maps are defined in `Program.cs` (`Specs`): the longitude/latitude box, width in tiles,
Natural Earth detail level, and the smallest island/lake to keep.

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
   Small maps read only the window they need, so the Ardennes gets WorldCover's full 10 m detail.
7. Small maps (the Ardennes) take their rivers and lakes from OpenStreetMap (`OsmWater`) instead:
   rivers `RiverMetres` wide, lakes over 1 ha. They are only drawn, as land cover: the tiles stay
   land, so they can be crossed and claimed. Nothing is carved out (`CarveLakes: false`).

## Credits (must be shown in the game's credits)

- Coastlines and lakes: **Natural Earth** (naturalearthdata.com), public domain.
- Land cover: **ESA WorldCover 2021** (CC BY 4.0). "© ESA WorldCover project 2021 /
  Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium."
- Rivers and lakes (Ardennes): **© OpenStreetMap contributors**, ODbL
  (openstreetmap.org/copyright). The map file is a produced work: credit is enough.
- Elevation: **Terrain Tiles** on AWS Open Data, made by Mapzen, with data from
  SRTM, GMTED2010, ETOPO1 and others. See
  https://github.com/tilezen/joerd/blob/master/docs/attribution.md
