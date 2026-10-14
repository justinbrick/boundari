# Downloads the free geographic data tools/MapBuilder needs into tools/data.
# Natural Earth (public domain) + AWS Terrain Tiles elevation (see MapBuilder/README.md for credits).
$ErrorActionPreference = 'Stop'
$data = Join-Path $PSScriptRoot 'data'
New-Item -ItemType Directory -Force (Join-Path $data 'terrarium3') | Out-Null

foreach ($name in 'ne_50m_land', 'ne_50m_lakes') {
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
Write-Host "Done: $data"
