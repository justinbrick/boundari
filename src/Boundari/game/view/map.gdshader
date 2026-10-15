shader_type canvas_item;

// Draws the whole map in a flat, cartographic style: solid land-cover colours
// with crisp edges, flat water with a light shoreline, optional hill shading,
// and territories on top. Every pixel is computed from smooth data, so
// shapes stay sharp and curved at any zoom.

// R = coast field (> 0 is land, coastline at 0), G = elevation (0..1).
uniform sampler2D terrain_tex : filter_linear, repeat_disable;
// Land cover type per cell (see LandCover.cs), cover_scale cells per tile side.
uniform sampler2D cover_tex : filter_nearest, repeat_disable;
// Average land-cover colour per tile, used when zoomed out too far to see single cells.
uniform sampler2D cover_avg_tex : filter_linear_mipmap, repeat_disable;
uniform bool has_cover = false;
uniform float cover_scale = 4.0;
// Colour of each CoverType, set from MapView.cs.
uniform vec3 cover_colors[7];
// Territory, split into owner_chunk-sized chunks (one texture-array layer each).
// RGBA8: RG = owner id, BA = previous owner id (0 = unclaimed, 65535 = water).
uniform sampler2DArray owner_tex : filter_nearest, repeat_disable;
// R32F: tick (with fraction) when each tile changed hands, for animating captures.
uniform sampler2DArray capture_tex : filter_nearest, repeat_disable;
uniform int owner_chunk = 256;
uniform int owner_chunks_x = 1;
// The moment being drawn, in ticks (one tick behind the simulation).
uniform float visual_time = 0.0;
// 256x256 palette, one pixel per owner id.
uniform sampler2D palette_tex : filter_nearest, repeat_disable;

uniform vec2 map_size = vec2(2000.0, 1250.0);
uniform float metres_per_tile = 2500.0;

group_uniforms topography;
uniform bool show_hillshade = true;
uniform float relief_strength : hint_range(0.0, 1.0) = 0.5;
uniform float relief_exaggeration : hint_range(0.0, 20.0) = 5.0;

group_uniforms water;
uniform vec4 water_color : source_color = vec4(0.37, 0.63, 0.78, 1.0);
// Thin solid line along every shore, and a very faint glow out into the water.
uniform vec4 shoreline_color : source_color = vec4(0.72, 0.89, 0.98, 1.0);
// Shoreline thickness in tiles (so it scales with the map), but never thinner than this many pixels.
uniform float shoreline_tiles : hint_range(0.05, 2.0) = 0.32;
uniform float shoreline_min_pixels : hint_range(0.5, 4.0) = 1.2;
uniform float shore_glow_strength : hint_range(0.0, 1.0) = 0.32;
uniform float shore_glow_tiles : hint_range(0.2, 6.0) = 2.5;

group_uniforms territory;
uniform bool show_territory = true;
uniform float territory_opacity : hint_range(0.0, 1.0) = 0.5;
// How far (in tiles) borders are smoothed. Bigger = rounder, smoother bends.
uniform float border_smoothing : hint_range(0.5, 2.0) = 1.0;
// How long (in ticks) a captured tile takes to fade to its new owner.
uniform float capture_fade : hint_range(0.05, 3.0) = 0.6;
// Border thickness, roughly in tiles.
uniform float border_width : hint_range(0.0, 1.0) = 0.3;
uniform float coast_border_width : hint_range(0.0, 3.0) = 0.6;
uniform float border_darken : hint_range(0.0, 1.0) = 0.35;

const int WATER = 65535;
const int FALLOUT = 65534; // land poisoned by a bomb (nobody owns it)
const int COVER_NONE = 0;
const int COVER_FARMLAND = 1;
const int COVER_MEADOW = 2;
const int COVER_ROCK = 4;
const int COVER_WATER = 6;

// ---------------------------------------------------------------------------
//  Sampling helpers
// ---------------------------------------------------------------------------

// Cubic B-spline weights for the 4 texels around a sample point.
void bspline_weights(float f, out float w[4]) {
	float f2 = f * f;
	float f3 = f2 * f;
	w[0] = (1.0 - 3.0 * f + 3.0 * f2 - f3) / 6.0;
	w[1] = (4.0 - 6.0 * f2 + 3.0 * f3) / 6.0;
	w[2] = (1.0 + 3.0 * f + 3.0 * f2 - 3.0 * f3) / 6.0;
	w[3] = f3 / 6.0;
}

// Smooth (bicubic B-spline) terrain sample from 4 bilinear taps. `tile` is in tile units.
vec2 terrain_at(vec2 tile) {
	vec2 p = tile - 0.5;
	vec2 i = floor(p);
	vec2 f = p - i;
	vec2 f2 = f * f;
	vec2 f3 = f2 * f;
	vec2 w0 = (1.0 - 3.0 * f + 3.0 * f2 - f3) / 6.0;
	vec2 w1 = (4.0 - 6.0 * f2 + 3.0 * f3) / 6.0;
	vec2 w2 = (1.0 + 3.0 * f + 3.0 * f2 - 3.0 * f3) / 6.0;
	vec2 w3 = f3 / 6.0;
	vec2 g0 = w0 + w1;
	vec2 g1 = w2 + w3;
	vec2 h0 = (i - 0.5 + w1 / g0) / map_size;
	vec2 h1 = (i + 1.5 + w3 / g1) / map_size;
	vec2 a = texture(terrain_tex, vec2(h0.x, h0.y)).rg;
	vec2 b = texture(terrain_tex, vec2(h1.x, h0.y)).rg;
	vec2 c = texture(terrain_tex, vec2(h0.x, h1.y)).rg;
	vec2 d = texture(terrain_tex, vec2(h1.x, h1.y)).rg;
	return g0.y * (g0.x * a + g1.x * b) + g1.y * (g0.x * c + g1.x * d);
}

// Inverse of ElevationScale.cs: 0..1 elevation back to metres.
float to_metres(float e) {
	if (e <= 0.0) {
		return 0.0;
	}
	if (e <= 0.55) {
		return e / 0.55 * 350.0;
	}
	if (e <= 0.85) {
		return 350.0 + (e - 0.55) / 0.30 * 1650.0;
	}
	return 2000.0 + (min(e, 1.0) - 0.85) / 0.15 * 3500.0;
}

int id_at(sampler2D ids_tex, ivec2 c, bool rg16) {
	vec2 rg = texelFetch(ids_tex, c, 0).rg;
	return rg16 ? int(rg.r * 255.0 + 0.5) + int(rg.g * 255.0 + 0.5) * 256 : int(rg.r * 255.0 + 0.5);
}

// Smooth "majority vote" over a grid of ids: each of the 4x4 nearby cells votes for
// its id with a B-spline weight. Gives curved, crisp-edged shapes instead of squares.
// Cells holding `skip` don't vote. `edge` = winner's lead over the runner-up
// (0 on a boundary, 1 deep inside one shape).
void vote(sampler2D ids_tex, ivec2 size, vec2 cell_pos, bool rg16, int skip,
		out int best_id, out int second_id, out float edge) {
	vec2 p = cell_pos - 0.5;
	vec2 pf = floor(p);
	ivec2 base = ivec2(pf);
	float wx[4];
	float wy[4];
	bspline_weights(p.x - pf.x, wx);
	bspline_weights(p.y - pf.y, wy);

	int ids[4] = int[4](0, 0, 0, 0);
	float ws[4] = float[4](0.0, 0.0, 0.0, 0.0);
	int count = 0;
	float total = 0.0;
	for (int j = 0; j < 4; j++) {
		for (int i = 0; i < 4; i++) {
			int id = id_at(ids_tex, clamp(base + ivec2(i - 1, j - 1), ivec2(0), size - 1), rg16);
			if (id == skip) {
				continue;
			}
			float w = wx[i] * wy[j];
			total += w;
			bool found = false;
			for (int k = 0; k < count; k++) {
				if (ids[k] == id) {
					ws[k] += w;
					found = true;
				}
			}
			if (!found && count < 4) {
				ids[count] = id;
				ws[count] = w;
				count++;
			}
		}
	}

	best_id = skip;
	second_id = skip;
	edge = 1.0;
	if (total <= 0.0) {
		return;
	}
	float w1 = -1.0;
	float w2 = -1.0;
	for (int k = 0; k < count; k++) {
		if (ws[k] > w1) {
			w2 = w1;
			second_id = best_id;
			w1 = ws[k];
			best_id = ids[k];
		} else if (ws[k] > w2) {
			w2 = ws[k];
			second_id = ids[k];
		}
	}
	if (w2 < 0.0) {
		second_id = best_id;
		w2 = 0.0;
	}
	edge = (w1 - w2) / total;
}

// ---------------------------------------------------------------------------
//  Territory
// ---------------------------------------------------------------------------

// Owner of a tile, its previous owner, and how far it has faded to the new owner
// (0 = still showing the previous owner, 1 = fully the new one).
void owner_at(ivec2 c, out int owner, out int previous, out float fade_in) {
	c = clamp(c, ivec2(0), ivec2(map_size) - 1);
	ivec3 cc = ivec3(c % owner_chunk, (c.y / owner_chunk) * owner_chunks_x + c.x / owner_chunk);
	vec4 o = texelFetch(owner_tex, cc, 0);
	owner = int(o.r * 255.0 + 0.5) + int(o.g * 255.0 + 0.5) * 256;
	previous = int(o.b * 255.0 + 0.5) + int(o.a * 255.0 + 0.5) * 256;
	float captured = texelFetch(capture_tex, cc, 0).r;
	fade_in = clamp((visual_time - captured) / capture_fade, 0.0, 1.0);
}

// Smooth territory lookup: nearby tiles vote for their owner with Gaussian weights
// over a ~6x6 area. Smoothing wider than one tile is what turns the tile staircase
// into clean curves. Tiles still fading in split their vote between the old and new
// owner, so fronts glide forward instead of jumping. `edge` = winner's lead over the
// runner-up: 0 on a border, 1 deep inside a territory.
void territory_at(vec2 tile, out int best_id, out float edge) {
	// Most pixels are deep inside one territory: check a sparse ring and skip the rest.
	ivec2 centre = ivec2(floor(tile));
	int id0;
	int prev0;
	float f0;
	owner_at(centre, id0, prev0, f0);
	bool settled = f0 >= 1.0;
	for (int j = -1; j <= 1 && settled; j++) {
		for (int i = -1; i <= 1 && settled; i++) {
			int id;
			int prev;
			float f;
			owner_at(centre + ivec2(i, j) * 2, id, prev, f);
			settled = id == id0 && f >= 1.0;
		}
	}
	best_id = id0;
	edge = 1.0;
	if (settled) {
		return;
	}

	ivec2 base = ivec2(floor(tile - 0.5)) - 2;
	float inv = 1.0 / (2.0 * border_smoothing * border_smoothing);
	float wx[6];
	float wy[6];
	for (int k = 0; k < 6; k++) {
		float dx = float(base.x + k) + 0.5 - tile.x;
		float dy = float(base.y + k) + 0.5 - tile.y;
		wx[k] = exp(-dx * dx * inv);
		wy[k] = exp(-dy * dy * inv);
	}

	int ids[6] = int[6](0, 0, 0, 0, 0, 0);
	float ws[6] = float[6](0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
	int count = 0;
	float total = 0.0;
	for (int j = 0; j < 6; j++) {
		for (int i = 0; i < 6; i++) {
			int id;
			int prev;
			float f;
			owner_at(base + ivec2(i, j), id, prev, f);
			if (id == WATER) {
				continue; // water doesn't compete, so territory reaches the coastline
			}
			float w = wx[i] * wy[j];
			total += w;
			for (int part = 0; part < 2; part++) {
				int who = part == 0 ? id : prev;
				float share = part == 0 ? w * f : w * (1.0 - f);
				if (share <= 0.0) {
					continue;
				}
				bool found = false;
				for (int k = 0; k < count; k++) {
					if (ids[k] == who) {
						ws[k] += share;
						found = true;
					}
				}
				if (!found && count < 6) {
					ids[count] = who;
					ws[count] = share;
					count++;
				}
			}
		}
	}

	best_id = WATER;
	if (total <= 0.0) {
		return;
	}
	float w1 = -1.0;
	float w2 = 0.0;
	for (int k = 0; k < count; k++) {
		if (ws[k] > w1) {
			w2 = max(w1, 0.0);
			w1 = ws[k];
			best_id = ids[k];
		} else if (ws[k] > w2) {
			w2 = ws[k];
		}
	}
	edge = (w1 - w2) / total;
}

// ---------------------------------------------------------------------------
//  Land colour
// ---------------------------------------------------------------------------

// Smooth value noise, for the mottled glow of fallout.
float fallout_hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float fallout_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(fallout_hash(i), fallout_hash(i + vec2(1.0, 0.0)), f.x),
		mix(fallout_hash(i + vec2(0.0, 1.0)), fallout_hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

vec3 cover_color_of(int id) {
	return cover_colors[clamp(id == COVER_NONE ? COVER_FARMLAND : id, 0, 6)];
}

// Land colour, plus how much of this pixel is small inland water from the land-cover
// layer (0..1). That water is drawn as water: no territory tint, no hill shading.
vec3 ground_color(vec2 tile, float elev, float tiles_per_px, out float water) {
	water = 0.0;
	if (!has_cover) {
		// No land-cover data (random maps, world map): flat bands by height.
		float fw = max(fwidth(elev), 1e-5);
		vec3 col = cover_colors[COVER_FARMLAND];
		col = mix(col, cover_colors[COVER_MEADOW], smoothstep(0.55 - fw, 0.55 + fw, elev));
		col = mix(col, cover_colors[COVER_ROCK], smoothstep(0.85 - fw, 0.85 + fw, elev));
		return col;
	}

	// Zoomed far out a single cell is smaller than a pixel: use the per-tile average instead.
	float cells_per_px = tiles_per_px * cover_scale;
	vec4 average = texture(cover_avg_tex, tile / map_size);
	if (cells_per_px > 1.3) {
		water = average.a;
		return average.rgb;
	}

	int best;
	int second;
	float edge;
	vote(cover_tex, textureSize(cover_tex, 0), tile * cover_scale, false, COVER_NONE, best, second, edge);
	float aa = max(fwidth(edge), 1e-4);
	float t = 0.5 + 0.5 * smoothstep(0.0, aa, edge);
	vec3 crisp = mix(cover_color_of(second), cover_color_of(best), t);
	float crisp_water = mix(second == COVER_WATER ? 1.0 : 0.0, best == COVER_WATER ? 1.0 : 0.0, t);
	float far = smoothstep(0.6, 1.3, cells_per_px);
	water = mix(crisp_water, average.a, far);
	return mix(crisp, average.rgb, far);
}

// ---------------------------------------------------------------------------

void fragment() {
	vec2 tile = UV * map_size;
	float tiles_per_px = max(length(dFdx(tile)), length(dFdy(tile)));

	vec2 t = terrain_at(tile);
	float h = t.r;
	float elev = t.g;
	vec2 tx0 = terrain_at(tile - vec2(1.0, 0.0));
	vec2 tx1 = terrain_at(tile + vec2(1.0, 0.0));
	vec2 ty0 = terrain_at(tile - vec2(0.0, 1.0));
	vec2 ty1 = terrain_at(tile + vec2(0.0, 1.0));

	// Anti-aliased land mask: about one screen pixel wide at every zoom level.
	float fw = max(fwidth(h), 1e-6);
	float land = smoothstep(-0.5 * fw, 0.5 * fw, h);

	float cover_water;
	vec3 ground = ground_color(tile, elev, tiles_per_px, cover_water);
	vec3 flat_ground = ground;

	if (show_hillshade) {
		// Soft hill shading, lit from the north-west.
		vec2 grad = vec2(to_metres(tx1.g) - to_metres(tx0.g), to_metres(ty1.g) - to_metres(ty0.g))
			* 0.5 / metres_per_tile * relief_exaggeration;
		vec3 normal = normalize(vec3(-grad, 1.0));
		vec3 light_dir = normalize(vec3(-1.0, -1.0, 1.5));
		float shade = clamp(dot(normal, light_dir) / light_dir.z, 0.45, 1.4);
		ground *= mix(1.0, shade, relief_strength);
	}

	ground = mix(ground, flat_ground, cover_water); // water stays flat
	vec3 color = mix(water_color.rgb, ground, land);

	// Distance to the shore in tiles (negative out in the water).
	float h_slope = max(length(vec2(tx1.r - tx0.r, ty1.r - ty0.r) * 0.5), 1e-5);
	float shore_dist = h / h_slope;
	float px = max(tiles_per_px, 1e-5);

	// Very subtle glow just offshore, never thinner than a few pixels.
	float glow_width = max(shore_glow_tiles, 5.0 * px);
	float glow = (1.0 - land) * exp(-max(-shore_dist, 0.0) / glow_width) * shore_glow_strength;
	color = mix(color, shoreline_color.rgb, glow);

	// Solid shoreline just on the water side: a set thickness in tiles, at least a pixel or so.
	float out_tiles = -shore_dist; // tiles out from the shore
	float line_width = max(shoreline_tiles, shoreline_min_pixels * px);
	float shore_line = smoothstep(-0.6 * px, 0.0, out_tiles)
		* (1.0 - smoothstep(line_width - 0.5 * px, line_width + 0.5 * px, out_tiles));
	color = mix(color, shoreline_color.rgb, shore_line);

	if (show_territory) {
		int id;
		float edge;
		territory_at(tile, id, edge);
		if (id == FALLOUT) {
			// Scorched ground with a slowly shifting, radioactive green glow (like OpenFront's fallout).
			vec3 scorched = texelFetch(palette_tex, ivec2(id & 255, id >> 8), 0).rgb;
			float n = fallout_noise(tile * 0.12 + vec2(visual_time * 0.004, 0.0)) * 0.65
				+ fallout_noise(tile * 0.45 - vec2(0.0, visual_time * 0.006)) * 0.35;
			vec3 glow = vec3(0.32, 0.86, 0.12);
			vec3 fallout = mix(scorched, glow, smoothstep(0.35, 0.95, n) * 0.7);
			color = mix(color, mix(ground * 0.4, fallout, 0.85), land * (1.0 - cover_water));
		} else if (id != WATER && id != 0) {
			vec4 swatch = texelFetch(palette_tex, ivec2(id & 255, id >> 8), 0);
			vec3 nation = swatch.rgb;
			// A palette alpha below 1 asks for a more solid fill (tribes are drawn opaque).
			float opacity = max(territory_opacity, 1.0 - swatch.a);

			// Distance to the coastline in tiles, so territories get an outline along the shore too.
			float coast_dist = shore_dist;

			// Borders never get thinner than ~1 pixel, so they don't flicker when zoomed out.
			float edge_aa = max(fwidth(edge), 1e-5);
			float bw = max(border_width, edge_aa * 1.2);
			float border = 1.0 - smoothstep(bw - edge_aa, bw + edge_aa, edge);
			float cbw = max(coast_border_width, tiles_per_px * 1.2);
			float coast_border = 1.0 - smoothstep(cbw - tiles_per_px, cbw + tiles_per_px, coast_dist);
			border = max(border, coast_border);

			vec3 fill = mix(ground, nation, opacity);
			vec3 outline = nation * (1.0 - border_darken);
			vec3 owned = mix(fill, outline, border);
			color = mix(color, owned, land * (1.0 - cover_water));
		}
	}

	COLOR = vec4(color, 1.0);
}
