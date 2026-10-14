"""
Builds a land-cover grid for a map from ESA WorldCover 2021 (10 m, global, CC BY 4.0).

WorldCover is stored as 3x3 degree cloud-optimised GeoTIFFs on AWS. Each file has
built-in reduced-resolution copies ("overviews"), so we read only a small reduced
copy over HTTP instead of downloading the full ~90 MB files.

Output: raw uint8 grid, `width` x `height`, row 0 = north edge, covering the bounding
box on a plain longitude/latitude grid. Values are WorldCover class codes
(10 tree cover, 20 shrubland, 30 grassland, 40 cropland, 50 built-up, 60 bare,
70 snow/ice, 80 water, 90 wetland, 95 mangroves, 100 moss/lichen), 0 = no data.

Usage (from the repo root, with the tools venv):
  tools/.venv/Scripts/python.exe tools/landcover/worldcover.py \
      --bbox -25 33 45 72 --size 8000 6936 --out tools/data/worldcover/europe.bin
"""

import argparse
import math
import os
import sys
from concurrent.futures import ThreadPoolExecutor

import numpy as np
import rasterio
from rasterio.errors import RasterioIOError
from rasterio.transform import from_bounds
from rasterio.warp import Resampling, reproject
from rasterio.windows import from_bounds as window_from_bounds

URL = "https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/ESA_WorldCover_10m_2021_v200_{name}_Map.tif"
SOURCE_DEGREES_PER_PIXEL = 1 / 12000  # 10 m
OVERVIEW_FACTORS = [2, 4, 8, 16, 32, 64]

# Don't let GDAL list S3 "directories"; only fetch the byte ranges we read.
os.environ.setdefault("GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR")
os.environ.setdefault("CPL_VSIL_CURL_ALLOWED_EXTENSIONS", ".tif")
os.environ.setdefault("GDAL_HTTP_MAX_RETRY", "4")
os.environ.setdefault("GDAL_HTTP_RETRY_DELAY", "2")


def tile_name(lat0, lon0):
    ns = "N" if lat0 >= 0 else "S"
    ew = "E" if lon0 >= 0 else "W"
    return f"{ns}{abs(lat0):02d}{ew}{abs(lon0):03d}"


def pick_overview(target_degrees):
    """Coarsest overview that is still at least ~2x finer than the target, so the
    'most common class' resampling has several source pixels per output cell."""
    best = None
    for i, factor in enumerate(OVERVIEW_FACTORS):
        if factor * SOURCE_DEGREES_PER_PIXEL * 2 <= target_degrees:
            best = i
    return best  # None = full resolution


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--bbox", nargs=4, type=float, metavar=("LON_MIN", "LAT_MIN", "LON_MAX", "LAT_MAX"), required=True)
    p.add_argument("--size", nargs=2, type=int, metavar=("WIDTH", "HEIGHT"), required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--workers", type=int, default=8)
    a = p.parse_args()

    lon_min, lat_min, lon_max, lat_max = a.bbox
    width, height = a.size
    dst_transform = from_bounds(lon_min, lat_min, lon_max, lat_max, width, height)
    target = min((lon_max - lon_min) / width, (lat_max - lat_min) / height)
    overview = pick_overview(target)
    factor = OVERVIEW_FACTORS[overview] if overview is not None else 1
    print(f"Grid {width}x{height}, {target * 111:.2f} km cells, reading 1/{factor} overviews", flush=True)

    out = np.zeros((height, width), dtype=np.uint8)
    tiles = [(lat0, lon0)
             for lat0 in range(int(math.floor(lat_min / 3) * 3), int(math.ceil(lat_max)), 3)
             for lon0 in range(int(math.floor(lon_min / 3) * 3), int(math.ceil(lon_max)), 3)]

    def fetch(tile):
        lat0, lon0 = tile
        # Part of the output grid this WorldCover file covers.
        win = window_from_bounds(max(lon0, lon_min), max(lat0, lat_min),
                                 min(lon0 + 3, lon_max), min(lat0 + 3, lat_max), dst_transform)
        win = win.round_offsets().round_lengths()
        row0, col0 = max(int(win.row_off), 0), max(int(win.col_off), 0)
        rows = min(int(win.height), height - row0)
        cols = min(int(win.width), width - col0)
        if rows <= 0 or cols <= 0:
            return tile, None
        try:
            kwargs = {} if overview is None else {"overview_level": overview}
            with rasterio.open(URL.format(name=tile_name(lat0, lon0)), **kwargs) as src:
                data = src.read(1)
                part = np.zeros((rows, cols), dtype=np.uint8)
                reproject(data, part,
                          src_transform=src.transform, src_crs=src.crs, src_nodata=0,
                          dst_transform=rasterio.windows.transform(rasterio.windows.Window(col0, row0, cols, rows), dst_transform),
                          dst_crs="EPSG:4326", dst_nodata=0, resampling=Resampling.mode)
            return tile, (row0, col0, part)
        except RasterioIOError:
            return tile, None  # no file: open ocean

    done = found = 0
    with ThreadPoolExecutor(max_workers=a.workers) as pool:
        for tile, result in pool.map(fetch, tiles):
            done += 1
            if result is not None:
                found += 1
                row0, col0, part = result
                view = out[row0:row0 + part.shape[0], col0:col0 + part.shape[1]]
                np.copyto(view, part, where=part != 0)
            if done % 20 == 0 or done == len(tiles):
                print(f"  {done}/{len(tiles)} tiles checked, {found} with land", flush=True)

    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    out.tofile(a.out)
    print(f"Wrote {a.out}", flush=True)


if __name__ == "__main__":
    sys.exit(main())
