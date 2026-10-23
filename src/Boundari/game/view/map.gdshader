shader_type canvas_item;

// Draws the whole map in a flat, cartographic style: solid land-cover colours
// with crisp edges, flat water with a light shoreline, optional hill shading,
// and territories on top. Every pixel is computed from smooth data, so
// shapes stay sharp and curved at any zoom.

// R = coast field (> 0 is land, coastline at 0), G = elevation (0..1),
// B = hill shade (worked out once per tile in MapView.cs), A = steepness of the coast field
// (to turn it into a distance to the shore). Precomputing B and A saves 16 texture reads a pixel.
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
// RGBA8: owner id and previous owner id in 12 bits each (R + low half of G, high half
// of G + B; 0 = unclaimed, 4095 = water), then A = capture fade length in 1/16 ticks.
uniform sampler2DArray owner_tex : filter_nearest, repeat_disable;
// R32F: tick (with fraction) when each tile changed hands, for animating captures.
uniform sampler2DArray capture_tex : filter_nearest, repeat_disable;
// R8 bits: 1 = hatched (land freshly taken from a player, see GameHatching.cs),
// 4 = near a finished defense post (only there does the shader look for posts).
uniform sampler2DArray hatch_tex : filter_nearest, repeat_disable;
// RGBA8: each tile's distance to its border in 1/dist_units tiles (255 = far): R before the
// latest change near it, G after, B and A the offset (+128) to the tile whose capture fade
// carries it from R to G. Kept by TerritoryTextures only while borders are too thick for
// the smoothing to measure (border_field).
uniform sampler2DArray dist_tex : filter_nearest, repeat_disable;
uniform float dist_units = 24.0;
uniform int owner_chunk = 256;
uniform int owner_chunks_x = 1;
// The moment being drawn, in ticks (one tick behind the simulation).
uniform float visual_time = 0.0;
// 256x256 palette, one pixel per owner id.
uniform sampler2D palette_tex : filter_nearest, repeat_disable;

uniform vec2 map_size = vec2(2000.0, 1250.0);

group_uniforms topography;
uniform float relief_strength : hint_range(0.0, 1.0) = 0.5;

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
// Border thickness in territory_at's `edge` units (its vote lead; erf of the distance in
// tiles over sqrt 2), set from MapView.BorderWidth. Up to 0.95, about 2 tiles.
uniform float border_width : hint_range(0.0, 1.0) = 0.3;
// Thicker borders: this many tiles, measured with dist_tex.
uniform bool border_field = false;
uniform float border_tiles = 0.4;
uniform float coast_border_width : hint_range(0.0, 3.0) = 0.6;
uniform float border_darken : hint_range(0.0, 1.0) = 0.35;
// The territory under the mouse (owner id; -1 = none) is drawn lighter.
uniform int hover_id = -1;
uniform float hover_lighten : hint_range(0.0, 1.0) = 0.08;
// The player a directed-attack arrow being dragged points at: lightened, with a gentle pulse.
uniform int target_id = -1;
uniform float target_lighten : hint_range(0.0, 1.0) = 0.3;
// Hatched land: diagonal stripes in the territory's border colour, this many tiles apart
// (set from MapView.cs). Zoomed out, the spacing doubles in steps (every other stripe
// narrows away) so stripes are never closer than hatch_min_pixels on screen and stay put.
uniform float hatch_tiles = 4.5;
uniform float hatch_min_pixels = 16.0;
uniform float hatch_strength : hint_range(0.0, 1.0) = 0.85;
// Share of each stripe period the stripe covers.
uniform float hatch_stripe_width : hint_range(0.1, 0.9) = 0.4;
// Hatched land settles this many ticks after it was taken (HatchRules.SettleTicks); each
// tile's stripes fade out over the last hatch_fade_ticks before that, so settling land
// sweeps away smoothly in the order it was taken instead of popping off tick by tick.
uniform float hatch_settle_ticks = 300.0;
uniform float hatch_fade_ticks = 10.0;
// Defense posts (OpenFront's BorderStampPass and TerritoryPass): their owner's land within a
// post's range is darkened, and its borders get a checkerboard - every other cell darkened.
// One texel per post: x, y, owner id.
uniform sampler2D defense_tex : filter_nearest, repeat_disable;
uniform int defense_count = 0;
uniform float defense_range = 30.0;
// OpenFront's territoryDefenseDarken (here on the whole fill, ground included).
uniform float defense_fill_darken : hint_range(0.0, 1.0) = 0.85;
// While placing a defense post: where it would go (x, y, owner id); its field is shown darkened.
uniform bool defense_preview_on = false;
uniform vec3 defense_preview = vec3(0.0);
// Checker cells are one OpenFront tile (set from MapView.cs), and defended borders are
// about that wide too, like OpenFront's (ours are thinner elsewhere); zoomed out the cells
// double in steps so they stay at least defense_min_pixels wide.
uniform float defense_cell_tiles = 1.0;
uniform float defense_min_pixels = 4.0;
// Defended border width, in territory_at's `edge` units: about one OpenFront tile
// (edge ~ erf(distance / (border_smoothing * sqrt 2)); 1.33 tiles on our Europe = 0.82).
uniform float defense_border_edge : hint_range(0.0, 1.0) = 0.8;
uniform float defense_checker_darken : hint_range(0.0, 1.0) = 0.7;

group_uniforms dev_tools;
// Land-cover patches: 1 = smooth rounded shapes (the normal look), 0 = square cells.
uniform float patch_shape : hint_range(0.0, 1.0) = 1.0;
// How far patches blend into their neighbours, in cells (0 = the normal, tight blend).
uniform float patch_morph : hint_range(0.0, 2.0) = 0.0;
// Contour lines every this many metres (0 = off).
uniform float contour_interval = 0.0;

const int WATER = 4095;
const int FALLOUT = 4094; // land poisoned by a bomb (nobody owns it)
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
vec4 terrain_at(vec2 tile) {
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
	vec4 a = texture(terrain_tex, vec2(h0.x, h0.y));
	vec4 b = texture(terrain_tex, vec2(h1.x, h0.y));
	vec4 c = texture(terrain_tex, vec2(h0.x, h1.y));
	vec4 d = texture(terrain_tex, vec2(h1.x, h1.y));
	return g0.y * (g0.x * a + g1.x * b) + g1.y * (g0.x * c + g1.x * d);
}

// Inverse of ElevationScale.cs: 0..1 elevation back to metres (for contour lines).
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

// Smooth "majority vote" over a grid of ids: nearby cells vote for their id, weighted
// by distance. Gives curved, crisp-edged shapes instead of squares. Normally a 4x4 cubic
// B-spline; with patch_morph, a wider Gaussian over 8x8 cells (patches blend further).
// patch_shape sharpens the weights towards "nearest cell only" (square corners).
// Cells holding `skip` don't vote. `edge` = winner's lead over the runner-up
// (0 on a boundary, 1 deep inside one shape).
void vote(sampler2D ids_tex, ivec2 size, vec2 cell_pos, bool rg16, int skip,
		out int best_id, out int second_id, out float edge) {
	vec2 p = cell_pos - 0.5;
	vec2 pf = floor(p);
	vec2 f = p - pf;
	ivec2 base = ivec2(pf);
	bool wide = patch_morph > 0.0;
	int lo = wide ? -3 : -1; // taps cover cells base+lo .. base+lo+n-1
	int n = wide ? 8 : 4;

	float wx[8];
	float wy[8];
	if (wide) {
		float inv = 1.0 / (2.0 * patch_morph * patch_morph);
		float sx = 0.0;
		float sy = 0.0;
		for (int k = 0; k < 8; k++) {
			float dx = float(lo + k) - f.x;
			float dy = float(lo + k) - f.y;
			wx[k] = exp(-dx * dx * inv);
			wy[k] = exp(-dy * dy * inv);
			sx += wx[k];
			sy += wy[k];
		}
		for (int k = 0; k < 8; k++) {
			wx[k] /= sx;
			wy[k] /= sy;
		}
	} else {
		float bx[4];
		float by[4];
		bspline_weights(f.x, bx);
		bspline_weights(f.y, by);
		for (int k = 0; k < 4; k++) {
			wx[k] = bx[k];
			wy[k] = by[k];
		}
	}
	if (patch_shape < 0.001) {
		// Square cells: all the weight on the cell this point is in.
		int nx = (f.x < 0.5 ? 0 : 1) - lo;
		int ny = (f.y < 0.5 ? 0 : 1) - lo;
		for (int k = 0; k < n; k++) {
			wx[k] = k == nx ? 1.0 : 0.0;
			wy[k] = k == ny ? 1.0 : 0.0;
		}
	} else if (patch_shape < 0.999) {
		// Tighter corners: raising the weights to a power favours the nearest cells more and
		// more, while staying smooth everywhere (no seams along cell midlines).
		float sharpen = pow(30.0, 1.0 - patch_shape);
		for (int k = 0; k < n; k++) {
			wx[k] = pow(max(wx[k], 0.0), sharpen);
			wy[k] = pow(max(wy[k], 0.0), sharpen);
		}
	}

	int ids[6] = int[6](0, 0, 0, 0, 0, 0);
	float ws[6] = float[6](0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
	int count = 0;
	float total = 0.0;
	for (int j = 0; j < n; j++) {
		for (int i = 0; i < n; i++) {
			float w = wx[i] * wy[j];
			if (w < 1e-4) {
				continue;
			}
			int id = id_at(ids_tex, clamp(base + ivec2(i + lo, j + lo), ivec2(0), size - 1), rg16);
			if (id == skip) {
				continue;
			}
			total += w;
			bool found = false;
			for (int k = 0; k < count; k++) {
				if (ids[k] == id) {
					ws[k] += w;
					found = true;
				}
			}
			if (!found && count < 6) {
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
	ivec4 b = ivec4(texelFetch(owner_tex, cc, 0) * 255.0 + 0.5);
	owner = b.r + (b.g & 15) * 256;
	previous = (b.g >> 4) + b.b * 16;
	float captured = texelFetch(capture_tex, cc, 0).r;
	float fade_length = float(b.a) / 16.0; // ticks
	fade_in = clamp((visual_time - captured) / fade_length, 0.0, 1.0);
}

// Just the owner of a tile (one texture read).
int owner_id_at(ivec2 c) {
	c = clamp(c, ivec2(0), ivec2(map_size) - 1);
	ivec4 b = ivec4(texelFetch(owner_tex, ivec3(c % owner_chunk, (c.y / owner_chunk) * owner_chunks_x + c.x / owner_chunk), 0) * 255.0 + 0.5);
	return b.r + (b.g & 15) * 256;
}

int hatch_bits(ivec2 c) {
	c = clamp(c, ivec2(0), ivec2(map_size) - 1);
	return int(texelFetch(hatch_tex, ivec3(c % owner_chunk, (c.y / owner_chunk) * owner_chunks_x + c.x / owner_chunk), 0).r * 255.0 + 0.5);
}

bool hatched_bit(ivec2 c) {
	return (hatch_bits(c) & 1) != 0;
}

// How hatched a tile still looks (0..1): fully until shortly before it settles, then fading
// out (a couple of ticks early, so it's gone before the simulation clears its bit).
float hatch_weight(ivec2 c) {
	if (!hatched_bit(c)) {
		return 0.0;
	}
	c = clamp(c, ivec2(0), ivec2(map_size) - 1);
	float taken = texelFetch(capture_tex, ivec3(c % owner_chunk, (c.y / owner_chunk) * owner_chunks_x + c.x / owner_chunk), 0).r;
	return clamp((taken + hatch_settle_ticks - 2.0 - visual_time) / hatch_fade_ticks, 0.0, 1.0);
}

// How much of `id`'s land around here is hatched (0..1), smoothed exactly like the territory
// borders (territory_at), so 0.5 is a clean curve where hatched land meets its owner's
// settled land. Only `id`'s own land counts, so it stays 1 out to the territory's edge,
// and tiles still fading in count by how far they've faded (a capture sweeping in isn't
// striped yet).
float hatch_cover(vec2 tile, int id) {
	// Most pixels have no hatched tile next to them at all.
	vec2 p = tile - 0.5;
	ivec2 c = ivec2(floor(p));
	if (!hatched_bit(c) && !hatched_bit(c + ivec2(1, 0)) && !hatched_bit(c + ivec2(0, 1)) && !hatched_bit(c + ivec2(1, 1))) {
		return 0.0;
	}
	// Deep inside hatched land (a sparse ring all hatched and settled, as in territory_at): fully hatched.
	ivec2 centre = ivec2(floor(tile));
	bool deep = true;
	for (int k = 0; k < 13 && deep; k++) {
		ivec2 o = k < 9 ? ivec2(k % 3 - 1, k / 3 - 1) * 2 : (k == 9 ? ivec2(1, 0) : k == 10 ? ivec2(-1, 0) : k == 11 ? ivec2(0, 1) : ivec2(0, -1));
		int owner;
		int prev;
		float fade_in;
		owner_at(centre + o, owner, prev, fade_in);
		deep = owner == id && fade_in >= 1.0 && hatch_weight(centre + o) >= 1.0;
	}
	if (deep) {
		return 1.0;
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
	float total = 0.0;
	float hatched = 0.0;
	for (int j = 0; j < 6; j++) {
		for (int i = 0; i < 6; i++) {
			ivec2 cc = base + ivec2(i, j);
			int owner;
			int prev;
			float fade_in;
			owner_at(cc, owner, prev, fade_in);
			if (owner != id) {
				continue;
			}
			float w = wx[i] * wy[j] * fade_in;
			total += w;
			hatched += w * hatch_weight(cc);
		}
	}
	return total > 1e-5 ? hatched / total : 0.0;
}

// How much this point is inside the range of one of `id`'s finished defense posts (0..1,
// anti-aliased along the circle's edge; the circle runs through the centres of the last
// tiles the post defends), and with `preview`, of the post being placed too.
float defense_cover(vec2 tile, int id, float tiles_per_px, bool preview) {
	float inside = 1e9; // distance past the nearest circle's edge, in tiles (negative = inside)
	if (defense_count > 0 && (hatch_bits(ivec2(floor(tile))) & 4) != 0) {
		for (int i = 0; i < defense_count; i++) {
			vec3 post = texelFetch(defense_tex, ivec2(i, 0), 0).rgb;
			if (int(post.z + 0.5) == id) {
				inside = min(inside, length(tile - post.xy - 0.5) - defense_range);
			}
		}
	}
	if (preview && defense_preview_on && int(defense_preview.z + 0.5) == id) {
		inside = min(inside, length(tile - defense_preview.xy - 0.5) - defense_range);
	}
	return 1.0 - smoothstep(-0.5 * tiles_per_px, 0.5 * tiles_per_px, inside);
}

// One tile's distance (tiles) to the edge of `id`'s territory as it's shown right now: its
// blend from before to after the latest change near it, moved along by the fade of the tile
// that was taken. A tile changing hands counts by how far it has faded (others' land is half
// a tile outside the line; land being taken from `id` was at its edge). Water: is_water.
float tile_border_distance(ivec2 c, int id, out bool is_water) {
	c = clamp(c, ivec2(0), ivec2(map_size) - 1);
	int owner;
	int prev;
	float fade_in;
	owner_at(c, owner, prev, fade_in);
	is_water = owner == WATER;
	float own = (owner == id ? fade_in : 0.0) + (prev == id ? 1.0 - fade_in : 0.0);
	if (own <= 0.0) {
		return -0.5;
	}
	float d = 0.5;
	if (owner == id) {
		vec4 v = texelFetch(dist_tex, ivec3(c % owner_chunk, (c.y / owner_chunk) * owner_chunks_x + c.x / owner_chunk), 0) * 255.0;
		int cause_owner;
		int cause_prev;
		float progress;
		owner_at(c + ivec2(int(v.b + 0.5) - 128, int(v.a + 0.5) - 128), cause_owner, cause_prev, progress);
		d = mix(v.r, v.g, progress) / dist_units;
	}
	return mix(-0.5, d, own);
}

// Distance (tiles) from this point inwards to the edge of `id`'s territory, smoothed over the
// 4x4 nearest tile centres (cubic B-spline, for round curves); water doesn't count (coasts
// have their own border). Far inside (further than any border shown), the nearest tile's.
float border_distance(vec2 tile, int id) {
	bool is_water;
	float near = tile_border_distance(ivec2(floor(tile)), id, is_water);
	if (near > border_tiles + 3.0) {
		return near;
	}
	vec2 p = tile - 0.5;
	ivec2 base = ivec2(floor(p)) - 1;
	vec2 f = p - floor(p);
	float wx[4];
	float wy[4];
	bspline_weights(f.x, wx);
	bspline_weights(f.y, wy);
	float total = 0.0;
	float weight = 0.0;
	for (int j = 0; j < 4; j++) {
		for (int i = 0; i < 4; i++) {
			float d = tile_border_distance(base + ivec2(i, j), id, is_water);
			if (is_water) {
				continue;
			}
			float w = wx[i] * wy[j];
			total += w * d;
			weight += w;
		}
	}
	return weight > 1e-5 ? total / weight : 1e3;
}

// Diagonal stripes `period` tiles apart: 1 on a stripe, 0 between, anti-aliased. Stripes sit
// on whole multiples of the period. `thin` (0..1) is how far along the zoom is to doubling
// the period: every other stripe narrows to nothing while the rest widen to keep their
// share of the doubled period, so the stripes stay crisp all the way (no half-faded ones).
float stripes(vec2 tile, float period, float tiles_per_px, float thin) {
	float u = (tile.x + tile.y) / period;
	float k = floor(u + 0.5);                  // the nearest stripe
	float tri = abs(u - k) * 2.0;              // 0 mid-stripe, 1 mid-gap
	bool going = abs(mod(k, 2.0)) > 0.5;       // the stripes that go when the period doubles
	float width = hatch_stripe_width * (going ? 1.0 - thin : 1.0 + thin);
	float aa = max(tiles_per_px / period, 1e-4); // (no fwidth: this runs in a branch)
	return width <= 0.0 ? 0.0 : 1.0 - smoothstep(width - aa, width + aa, tri);
}

// Smooth territory lookup: nearby tiles vote for their owner with Gaussian weights
// over a ~6x6 area. Smoothing wider than one tile is what turns the tile staircase
// into clean curves. Tiles still fading in split their vote between the old and new
// owner, so fronts glide forward instead of jumping. `edge` = winner's lead over the
// runner-up: 0 on a border, 1 deep inside a territory.
void territory_at(vec2 tile, out int best_id, out float edge) {
	// Most pixels are deep inside one territory: check a sparse ring and skip the rest.
	// Every tile checked must also have finished fading in: a freshly captured tile already
	// has its new owner but still looks like the old one, and treating it as settled made
	// expanding fronts snap to hard tile steps with no border.
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
	// The ring skips the tiles right next to this one, which are the likeliest to be mid-fade.
	for (int k = 0; k < 4 && settled; k++) {
		ivec2 step = k == 0 ? ivec2(1, 0) : k == 1 ? ivec2(-1, 0) : k == 2 ? ivec2(0, 1) : ivec2(0, -1);
		int id;
		int prev;
		float f;
		owner_at(centre + step, id, prev, f);
		settled = id == id0 && f >= 1.0;
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

	vec4 t = terrain_at(tile);
	float h = t.r;
	float elev = t.g;

	// Anti-aliased land mask: about one screen pixel wide at every zoom level.
	float fw = max(fwidth(h), 1e-6);
	float land = smoothstep(-0.5 * fw, 0.5 * fw, h);

	float cover_water;
	vec3 ground = ground_color(tile, elev, tiles_per_px, cover_water);
	vec3 flat_ground = ground;

	// Soft hill shading, lit from the north-west (precomputed per tile). Always on.
	ground *= mix(1.0, t.b, relief_strength);

	if (contour_interval > 0.0) {
		// Contour lines (a dev-tool option): thin brown lines at every contour_interval metres.
		float metres = to_metres(elev);
		float c = metres / contour_interval;
		float d = abs(fract(c + 0.5) - 0.5) / max(fwidth(c), 1e-5);
		float line = (1.0 - smoothstep(0.5, 1.5, d)) * step(contour_interval * 0.5, metres);
		ground = mix(ground, vec3(0.36, 0.28, 0.18), line * 0.45);
	}

	ground = mix(ground, flat_ground, cover_water); // water stays flat
	vec3 color = mix(water_color.rgb, ground, land);

	// Distance to the shore in tiles (negative out in the water).
	float h_slope = max(t.a, 1e-5);
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

			// Hatched land: stripes in the border colour, spaced in whole doublings of
			// hatch_tiles, cross-fading between the two nearest as the zoom changes, and an
			// outline as thick as the borders along the inside of its edge, where it meets the
			// owner's settled land (the territory's own border covers its outer edge).
			float hatch = hatch_cover(tile, id);
			float stripe = 0.0;
			float hatch_edge = 0.0;
			if (hatch > 0.0) {
				float lead = 2.0 * hatch - 1.0;  // like territory_at's edge: 0 on the line, 1 deep inside
				float aa = 0.8 * tiles_per_px / border_smoothing; // its slope near the line, per pixel
				float inside = smoothstep(-aa, aa, lead);
				float lod = max(log2(hatch_min_pixels * tiles_per_px / hatch_tiles), 0.0);
				float fine = hatch_tiles * exp2(floor(lod));
				stripe = stripes(tile, fine, tiles_per_px, smoothstep(0.0, 1.0, fract(lod)));
				stripe *= inside * hatch_strength;
				float hbw = max(border_width, aa * 1.2);
				hatch_edge = border_width > 0.0 ? inside * (1.0 - smoothstep(hbw - aa, hbw + aa, lead)) : 0.0;
			}

			// Distance to the coastline in tiles, so territories get an outline along the shore too.
			float coast_dist = shore_dist;

			// Borders never get thinner than ~1 pixel, so they don't flicker when zoomed out.
			float edge_aa = max(fwidth(edge), 1e-5);
			float bw = max(border_width, edge_aa * 1.2);
			// Defense posts: their land in range is darkened (and while placing one, the land it
			// would protect), and their borders there are widened and checkered.
			float posts = defense_count > 0 ? defense_cover(tile, id, tiles_per_px, false) : 0.0;
			float cover = defense_preview_on ? defense_cover(tile, id, tiles_per_px, true) : posts;
			// Thicker borders than the smoothing can measure use each tile's distance to the border.
			float border_dist = border_field ? border_distance(tile, id) : 1e3;
			bool defense = posts > 0.5 && (edge < max(bw, defense_border_edge) + edge_aa || border_dist < border_tiles + tiles_per_px);
			if (defense) {
				bw = max(bw, defense_border_edge);
			}
			float border = 1.0 - smoothstep(bw - edge_aa, bw + edge_aa, edge);
			if (border_field) {
				border = max(border, 1.0 - smoothstep(border_tiles - 0.5 * tiles_per_px, border_tiles + 0.5 * tiles_per_px, border_dist));
			}
			float cbw = max(coast_border_width, tiles_per_px * 1.2);
			float coast_border = 1.0 - smoothstep(cbw - tiles_per_px, cbw + tiles_per_px, coast_dist);
			border = border_width > 0.0 ? max(max(border, coast_border), hatch_edge) : 0.0;

			vec3 fill = mix(ground, nation, opacity);
			fill *= mix(1.0, defense_fill_darken, cover);
			vec3 outline = nation * (1.0 - border_darken);
			if (defense && border > 0.0) {
				float cell = defense_cell_tiles * exp2(max(ceil(log2(defense_min_pixels * tiles_per_px / defense_cell_tiles)), 0.0));
				ivec2 c = ivec2(floor(tile / cell));
				if (((c.x + c.y) & 1) == 1) {
					outline *= defense_checker_darken;
				}
			}
			vec3 owned = mix(mix(fill, outline, stripe), outline, border);
			if (id == hover_id) {
				owned = mix(owned, vec3(1.0), hover_lighten);
			}
			if (id == target_id) {
				owned = mix(owned, vec3(1.0), target_lighten * (0.75 + 0.25 * sin(visual_time * 0.6)));
			}
			color = mix(color, owned, land * (1.0 - cover_water));
		}
	}

	COLOR = vec4(color, 1.0);
}
