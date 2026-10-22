# Downloads the free geographic data tools/MapBuilder needs into tools/data.
# Natural Earth (public domain), AWS Terrain Tiles elevation and OpenStreetMap water (see MapBuilder/README.md for credits).
$ErrorActionPreference = 'Stop'
$data = Join-Path $PSScriptRoot 'data'
New-Item -ItemType Directory -Force (Join-Path $data 'terrarium3') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $data 'terrarium6') | Out-Null

# Coastlines and lakes (50m detail for the world map, 10m for regional maps) and rivers.
foreach ($name in 'ne_50m_land', 'ne_50m_lakes', 'ne_10m_land', 'ne_10m_lakes', 'ne_10m_rivers_lake_centerlines') {
    $url = "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/$name.geojson"
    Invoke-WebRequest $url -OutFile (Join-Path $data "$name.geojson")
}

# Elevation, zoom level 3: 8x8 tiles of 256x256 covering the whole world.
foreach ($x in 0..7) {
    foreach ($y in 0..7) {
        $url = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/3/$x/$y.png"
        Invoke-WebRequest $url -OutFile (Join-Path $data "terrarium3\${x}_$y.png")
    }
}
# Elevation, zoom level 6, only the tiles covering the Europe map.
foreach ($x in 27..40) {
    foreach ($y in 13..25) {
        $url = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/6/$x/$y.png"
        Invoke-WebRequest $url -OutFile (Join-Path $data "terrarium6\${x}_$y.png")
    }
}
# Elevation, zoom level 12, only the tiles covering the Ardennes map (~25 m detail).
New-Item -ItemType Directory -Force (Join-Path $data 'terrarium12') | Out-Null
foreach ($x in 2110..2116) {
    foreach ($y in 1384..1390) {
        $url = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/12/$x/$y.png"
        Invoke-WebRequest $url -OutFile (Join-Path $data "terrarium12\${x}_$y.png")
    }
}

# Rivers, streams and lakes around the Ardennes map from OpenStreetMap (ODbL), ~11 MB.
New-Item -ItemType Directory -Force (Join-Path $data 'osm') | Out-Null
$query = '[out:json][timeout:120];(way["waterway"~"^(river|stream|canal)$"](49.90,5.45,50.28,6.05);' +
         'way["natural"="water"](49.90,5.45,50.28,6.05);relation["natural"="water"](49.90,5.45,50.28,6.05););out geom;'
Invoke-WebRequest 'https://overpass-api.de/api/interpreter' -Method Post -Body @{ data = $query } `
    -UserAgent 'BoundariMapBuilder/1.0' -OutFile (Join-Path $data 'osmrdennes_water.json')
Write-Host "Done: $data"
