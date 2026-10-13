extends Node2D

# ============================================================
#  TERRAIN GENERATOR
#
#  Single heightmap drives ocean/land, terrain difficulty,
#  and shaded relief. Original ridged multifractal + edge
#  falloff approach — produces one central landmass.
#
#  Terrain difficulty is HEIGHT-based, not biome-based:
#    FLATLAND   — low land, easiest crossing
#    HILLS      — mid elevation, moderate penalty
#    MOUNTAINS  — high elevation, hardest to cross
#  Biome (grassland vs desert) is purely visual.
#
#  DUAL TEXTURE OUTPUT:
#    Texture 0 — FORMAT_RF (32-bit float, full precision heightmap)
#      R = heightmap (0=deep ocean, ocean_threshold=shore, 1=peak)
#    Texture 1 — FORMAT_RGBA8 (8-bit, data that doesn't need precision)
#      R = biome value (0=grassland, 1=desert)
#      G = patch noise (surface detail)
#      B = unused
#      A = unused
#
#  Splitting into two textures gives the Sobel operator in the shader
#  full float precision — no more 8-bit quantization banding.
# ============================================================

enum TerrainType { OCEAN, FLATLAND, HILLS, MOUNTAINS, DESERT_FLAT, DESERT_HILLS, DESERT_MOUNTAINS }

@export var map_width:  int   = 2048
@export var map_height: int   = 2048
@export var cell_size:  int   = 8
@export var noise_seed: int   = 42

## Sea level — heights below this are ocean
@export var ocean_threshold: float = 0.42

## Warp strength — distorts coastlines
@export var warp_amplitude: float = 60.0

## Minimum landmass size in cells
@export var min_landmass_cells: int = 40

## Grassland/desert transition strip width
@export var biome_softness: float = 0.18

# Land height bands (normalised 0–1 above ocean_threshold):
const HILL_THRESHOLD:     float = 0.20
const MOUNTAIN_THRESHOLD: float = 0.52

var terrain_map:  Dictionary = {}
var landmass_map: Dictionary = {}

var _height_cache: PackedFloat32Array
var _biome_cache:  PackedFloat32Array
var _patch_cache:  PackedFloat32Array
var _ocean_cache:  PackedFloat32Array

const BASE_SIZE: float = 2048.0


func _ready() -> void:
	generate()


func generate() -> void:
	print("Generating %dx%d..." % [map_width, map_height])
	var t := Time.get_ticks_msec()
	_build_caches()
	_build_terrain_map()
	_flood_fill_landmasses()
	_remove_small_landmasses()
	_apply_texture()
	print("Done in %.2fs — Landmasses: %d" % [
		(Time.get_ticks_msec() - t) / 1000.0, _count_landmasses()])


# ─────────────────────────────────────────────────────────────
#  CACHE BUILDING
# ─────────────────────────────────────────────────────────────
func _build_caches() -> void:
	var noise_scale := BASE_SIZE / float(max(map_width, map_height))
	var t0 := Time.get_ticks_msec()

	# Domain warp
	var n_wx := _make_perlin(noise_seed + 1, 4, 0.006 * noise_scale)
	var n_wy := _make_perlin(noise_seed + 2, 4, 0.006 * noise_scale)
	var f_wx := _to_floats(n_wx.get_image(map_width, map_height))
	var f_wy := _to_floats(n_wy.get_image(map_width, map_height))

	# Biome — very low freq, continent-scale grassland/desert blobs
	var n_biome := _make_perlin(noise_seed + 50, 3, 0.0008 * noise_scale)
	var f_biome := _to_floats(n_biome.get_image(map_width, map_height))

	# Border warp — mid freq, used to make biome boundary jagged
	var n_border := _make_perlin(noise_seed + 75, 3, 0.004 * noise_scale)
	var f_border := _to_floats(n_border.get_image(map_width, map_height))

	# Surface patch detail — per-biome texture variation
	var n_patch := _make_perlin(noise_seed + 200, 4, 0.005 * noise_scale)
	var f_patch := _to_floats(n_patch.get_image(map_width, map_height))

	# Ocean floor — gives shallow shores and deep open water
	var n_ocean := _make_perlin(noise_seed + 300, 3, 0.003 * noise_scale)
	var f_ocean := _to_floats(n_ocean.get_image(map_width, map_height))

	# Ridged multifractal heightmap — 5 octaves
	var freqs: Array = [
		0.0025 * noise_scale,
		0.007  * noise_scale,
		0.020  * noise_scale,
		0.055  * noise_scale,
		0.150  * noise_scale,
	]
	var images: Array = []
	for fi in range(freqs.size()):
		var n := _make_perlin(noise_seed + 10 + fi * 7, 1, freqs[fi])
		images.append(_to_floats(n.get_image(map_width, map_height)))

	print("  Images: %.2fs" % [(Time.get_ticks_msec() - t0) / 1000.0])
	t0 = Time.get_ticks_msec()

	var size := map_width * map_height
	_height_cache.resize(size)
	_biome_cache.resize(size)
	_patch_cache.resize(size)
	_ocean_cache.resize(size)

	var amplitudes := [0.5, 0.25, 0.125, 0.0625, 0.03125]

	for y in range(map_height):
		for x in range(map_width):
			var i := y * map_width + x

			var wx: int = clampi(x + int(f_wx[i] * warp_amplitude), 0, map_width  - 1)
			var wy: int = clampi(y + int(f_wy[i] * warp_amplitude), 0, map_height - 1)
			var wi := wy * map_width + wx

			# Ridged multifractal
			var height: float = 0.0
			var weight: float = 1.0
			for oi in range(images.size()):
				var raw:   float = images[oi][wi] * 2.0 - 1.0
				var ridge: float = 1.0 - abs(raw)
				ridge  = ridge * ridge
				ridge *= weight
				height += ridge * amplitudes[oi]
				weight  = clamp(ridge * 2.0, 0.0, 1.0)
			height = clamp(height / 0.9, 0.0, 1.0)

			# Edge falloff — border becomes ocean
			var ex: float = float(x) / float(map_width)
			var ey: float = float(y) / float(map_height)
			var edge_dist: float = min(min(ex, 1.0 - ex), min(ey, 1.0 - ey)) * 4.0
			height *= smoothstep(0.0, 1.0, clamp(edge_dist, 0.0, 1.0))

			# Store ocean floor value for every pixel so removed
			# landmasses can be properly replaced with ocean depth later
			_ocean_cache[i] = f_ocean[wi] * ocean_threshold * 0.88

			# Ocean floor: replace sub-threshold height with depth noise
			if height < ocean_threshold:
				height = _ocean_cache[i]

			_height_cache[i] = height

			# Bake border warp into biome so shader gets a pre-distorted
			# boundary — no nibble unpacking, no quantization bands.
			var warped_biome: float = clamp(f_biome[i] + (f_border[wi] - 0.5) * 0.24, 0.0, 1.0)
			_biome_cache[i] = warped_biome

			# Patch noise stored full 8-bit in alpha — no quantization
			_patch_cache[i] = f_patch[wi]

	print("  Cache: %.2fs" % [(Time.get_ticks_msec() - t0) / 1000.0])


func _make_perlin(seed_val: int, octaves: int, freq: float) -> FastNoiseLite:
	var n := FastNoiseLite.new()
	n.seed            = seed_val
	n.noise_type      = FastNoiseLite.TYPE_PERLIN
	n.fractal_type    = FastNoiseLite.FRACTAL_NONE
	n.fractal_octaves = octaves
	n.frequency       = freq
	return n


func _to_floats(img: Image) -> PackedFloat32Array:
	img.convert(Image.FORMAT_RF)
	return img.get_data().to_float32_array()


# ─────────────────────────────────────────────────────────────
#  TERRAIN MAP
# ─────────────────────────────────────────────────────────────
func _build_terrain_map() -> void:
	terrain_map.clear()
	@warning_ignore("integer_division")
	var cols: int = map_width / cell_size
	@warning_ignore("integer_division")
	var rows: int = map_height / cell_size
	for row in range(rows):
		for col in range(cols):
			var px: int = col * cell_size + cell_size >> 1
			var py: int = row * cell_size + cell_size >> 1
			var i  := py * map_width + px
			var h: float = _height_cache[i]
			var b: float = _biome_cache[i]
			var cell := Vector2i(col, row)

			if h < ocean_threshold:
				terrain_map[cell] = TerrainType.OCEAN
				continue

			var land_h: float = clamp(
				(h - ocean_threshold) / (1.0 - ocean_threshold), 0.0, 1.0)
			var is_desert: bool = b > 0.5

			if land_h < HILL_THRESHOLD:
				terrain_map[cell] = TerrainType.DESERT_FLAT      if is_desert else TerrainType.FLATLAND
			elif land_h < MOUNTAIN_THRESHOLD:
				terrain_map[cell] = TerrainType.DESERT_HILLS     if is_desert else TerrainType.HILLS
			else:
				terrain_map[cell] = TerrainType.DESERT_MOUNTAINS if is_desert else TerrainType.MOUNTAINS


# ─────────────────────────────────────────────────────────────
#  FLOOD FILL
# ─────────────────────────────────────────────────────────────
func _flood_fill_landmasses() -> void:
	landmass_map.clear()
	@warning_ignore("integer_division")
	var cols: int = map_width / cell_size
	@warning_ignore("integer_division")
	var rows: int = map_height / cell_size
	var current_id := 1
	for row in range(rows):
		for col in range(cols):
			var cell := Vector2i(col, row)
			if terrain_map[cell] == TerrainType.OCEAN: continue
			if landmass_map.has(cell): continue
			_flood_fill(cell, current_id, cols, rows)
			current_id += 1


func _flood_fill(start: Vector2i, id: int, cols: int, rows: int) -> void:
	var stack: Array[Vector2i] = [start]
	while stack.size() > 0:
		var cell: Vector2i = stack.pop_back()
		if landmass_map.has(cell): continue
		if not terrain_map.has(cell): continue
		if terrain_map[cell] == TerrainType.OCEAN: continue
		landmass_map[cell] = id
		for offset in [Vector2i(1,0), Vector2i(-1,0), Vector2i(0,1), Vector2i(0,-1), Vector2i(1,1), Vector2i(-1,1), Vector2i(1,-1), Vector2i(-1,-1)]:
			var nb: Vector2i = cell + offset
			if nb.x >= 0 and nb.x < cols and nb.y >= 0 and nb.y < rows:
				if not landmass_map.has(nb):
					stack.push_back(nb)


# ─────────────────────────────────────────────────────────────
#  REMOVE SMALL LANDMASSES
# ─────────────────────────────────────────────────────────────
func _remove_small_landmasses() -> void:
	var sizes: Dictionary = {}
	for cell in landmass_map:
		sizes[landmass_map[cell]] = sizes.get(landmass_map[cell], 0) + 1

	var removed: Array[Vector2i] = []
	for cell in landmass_map.keys():
		if sizes[landmass_map[cell]] < min_landmass_cells:
			removed.append(cell)

	# Collect removed cell pixel bounds with a 1-cell border padding
	# so the coast AA bleed in the shader has no land height to reveal.
	var removed_set: Dictionary = {}
	for cell in removed:
		removed_set[cell] = true

	for cell in removed:
		terrain_map[cell] = TerrainType.OCEAN
		landmass_map.erase(cell)

	# Replace heights — iterate with padding so border pixels are also
	# overwritten, eliminating any coast AA artifacts at cell edges.
	for cell in removed:
		for dy in range(-1, cell_size + 1):
			for dx in range(-1, cell_size + 1):
				var px: int = cell.x * cell_size + dx
				var py: int = cell.y * cell_size + dy
				if px >= 0 and px < map_width and py >= 0 and py < map_height:
					var pi := py * map_width + px
					# Only overwrite if this pixel isn't part of a kept landmass
					@warning_ignore("integer_division")
					var ncell := Vector2i(px / cell_size, py / cell_size)
					if not landmass_map.has(ncell):
						_height_cache[pi] = _ocean_cache[pi]


# ─────────────────────────────────────────────────────────────
#  TEXTURE OUTPUT — dual texture
# ─────────────────────────────────────────────────────────────
func _apply_texture() -> void:
	var size := map_width * map_height

	# ── Texture 0: heightmap — FORMAT_RF (32-bit float per pixel) ──
	# Full float precision — no quantization, Sobel gets clean gradients.
	var height_bytes := PackedByteArray()
	height_bytes.resize(size * 4)
	for i in range(size):
		var packed := PackedFloat32Array([_height_cache[i]])
		var b := packed.to_byte_array()
		height_bytes[i * 4]     = b[0]
		height_bytes[i * 4 + 1] = b[1]
		height_bytes[i * 4 + 2] = b[2]
		height_bytes[i * 4 + 3] = b[3]

	var height_img := Image.create_from_data(
		map_width, map_height, false, Image.FORMAT_RF, height_bytes)

	# ── Texture 1: data — FORMAT_RGBA8 ────────────────────────
	# Biome and patch don't need float precision.
	var data_bytes := PackedByteArray()
	data_bytes.resize(size * 4)
	for i in range(size):
		var b := i * 4
		data_bytes[b]     = int(_biome_cache[i] * 255.0)  # R = biome
		data_bytes[b + 1] = int(_patch_cache[i] * 255.0)  # G = patch
		data_bytes[b + 2] = 0
		data_bytes[b + 3] = 255

	var data_img := Image.create_from_data(
		map_width, map_height, false, Image.FORMAT_RGBA8, data_bytes)

	# ── Push to MapDisplay ────────────────────────────────────
	var display := get_node_or_null("MapDisplay")
	if display == null:
		push_warning("No 'MapDisplay' child found.")
		return
	if display is Sprite2D and display.material is ShaderMaterial:
		var mat := display.material as ShaderMaterial
		display.texture = ImageTexture.create_from_image(height_img)
		mat.set_shader_parameter("data_texture",       ImageTexture.create_from_image(data_img))
		mat.set_shader_parameter("ocean_threshold",    ocean_threshold)
		mat.set_shader_parameter("biome_softness",     biome_softness)
		mat.set_shader_parameter("hill_threshold",     HILL_THRESHOLD)
		mat.set_shader_parameter("mountain_threshold", MOUNTAIN_THRESHOLD)


# ─────────────────────────────────────────────────────────────
#  HELPERS
# ─────────────────────────────────────────────────────────────
func _count_landmasses() -> int:
	var ids := {}
	for cell in landmass_map:
		ids[landmass_map[cell]] = true
	return ids.size()


# ─────────────────────────────────────────────────────────────
#  PUBLIC API
# ─────────────────────────────────────────────────────────────
func get_terrain_at_pixel(world_pos: Vector2) -> TerrainType:
	@warning_ignore("integer_division")
	var cell := Vector2i(int(world_pos.x) / cell_size, int(world_pos.y) / cell_size)
	return terrain_map.get(cell, TerrainType.OCEAN)

func can_own(world_pos: Vector2) -> bool:
	return get_terrain_at_pixel(world_pos) != TerrainType.OCEAN

func get_landmass_id(world_pos: Vector2) -> int:
	@warning_ignore("integer_division")
	var cell := Vector2i(int(world_pos.x) / cell_size, int(world_pos.y) / cell_size)
	return landmass_map.get(cell, 0)

## Movement difficulty multiplier. 1.0 = base speed, INF = impassable.
func get_difficulty(world_pos: Vector2) -> float:
	match get_terrain_at_pixel(world_pos):
		TerrainType.FLATLAND:          return 1.0
		TerrainType.DESERT_FLAT:       return 1.3
		TerrainType.HILLS:             return 1.8
		TerrainType.DESERT_HILLS:      return 2.2
		TerrainType.MOUNTAINS:         return 3.5
		TerrainType.DESERT_MOUNTAINS:  return 4.0
		_:                             return INF

func same_landmass(pos_a: Vector2, pos_b: Vector2) -> bool:
	var id_a := get_landmass_id(pos_a)
	var id_b := get_landmass_id(pos_b)
	return id_a != 0 and id_a == id_b


