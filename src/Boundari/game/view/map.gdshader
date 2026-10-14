shader_type canvas_item;

// Draws the whole map in a flat, cartographic style: solid land-cover colours
// with crisp edges, flat water, optional hill shading and contour lines
// (toggled separately), and territories on top. Every pixel is computed from smooth data, so
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
// Owner id per tile, 16-bit in RG8. 0 = unclaimed land, 65535 = water.
uniform sampler2D owner_tex : filter_nearest, repeat_disable;
// 256x256 palette, one pixel per owner id.
uniform sampler2D palette_tex : filter_nearest, repeat_disable;

uniform vec2 map_size = vec2(2000.0, 1250.0);
uniform float metres_per_tile = 2500.0;

group_uniforms topography;
uniform bool show_hillshade = true;
uniform bool show_contours = true;
uniform float relief_strength : hint_range(0.0, 1.0) = 0.5;
uniform float relief_exaggeration : hint_range(0.0, 20.0) = 5.0;
// Metres between contour lines (doubles automatically when lines get too dense).
uniform float contour_interval = 100.0;
uniform float contour_opacity : hint_range(0.0, 1.0) = 0.3;

group_uniforms water;
uniform vec4 water_color : source_color = vec4(0.37, 0.63, 0.78, 1.0);
uniform vec4 coastline_color : source_color = vec4(0.16, 0.30, 0.40, 1.0);
uniform float coastline_opacity : hint_range(0.0, 1.0) = 0.35;

group_uniforms territory;
uniform bool show_territory = true;
uniform float territory_opacity : hint_range(0.0, 1.0) = 0.5;
// Border thickness, roughly in tiles.
uniform float border_width : hint_range(0.0, 1.0) = 0.45;
uniform float coast_border_width : hint_range(0.0, 3.0) = 0.6;
uniform float border_darken : hint_range(0.0, 1.0) = 0.35;

const int WATER = 65535;
const int COVER_NONE = 0;
const int COVER_FARMLAND = 1;
const int COVER_MEADOW = 2;
const int COVER_ROCK = 4;

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
//  Land colour
// ---------------------------------------------------------------------------

vec3 cover_color_of(int id) {
	return cover_colors[clamp(id == COVER_NONE ? COVER_FARMLAND : id, 0, 6)];
}

vec3 ground_color(vec2 tile, float elev, float tiles_per_px) {
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
	vec3 average = texture(cover_avg_tex, tile / map_size).rgb;
	if (cells_per_px > 1.3) {
		return average;
	}

	int best;
	int second;
	float edge;
	vote(cover_tex, textureSize(cover_tex, 0), tile * cover_scale, false, COVER_NONE, best, second, edge);
	float aa = max(fwidth(edge), 1e-4);
	vec3 crisp = mix(cover_color_of(second), cover_color_of(best), 0.5 + 0.5 * smoothstep(0.0, aa, edge));
	return mix(crisp, average, smoothstep(0.6, 1.3, cells_per_px));
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

	vec3 ground = ground_color(tile, elev, tiles_per_px);

	if (show_hillshade) {
		// Soft hill shading, lit from the north-west.
		vec2 grad = vec2(to_metres(tx1.g) - to_metres(tx0.g), to_metres(ty1.g) - to_metres(ty0.g))
			* 0.5 / metres_per_tile * relief_exaggeration;
		vec3 normal = normalize(vec3(-grad, 1.0));
		vec3 light_dir = normalize(vec3(-1.0, -1.0, 1.5));
		float shade = clamp(dot(normal, light_dir) / light_dir.z, 0.45, 1.4);
		ground *= mix(1.0, shade, relief_strength);
	}

	if (show_contours) {
		// Contour lines, about one pixel wide. The spacing doubles when zoomed out so
		// lines never crowd closer than a few pixels.
		float m = to_metres(elev);
		float fwm = max(fwidth(m), 1e-3);
		float interval = contour_interval * exp2(max(0.0, ceil(log2(fwm * 6.0 / contour_interval))));
		float nearest = round(m / interval);
		float dist = abs(m - nearest * interval);
		float line = 1.0 - smoothstep(0.4 * fwm, 1.2 * fwm, dist);
		line *= step(0.5, nearest); // the 0 m line is the coastline
		float major = mod(nearest, 5.0) < 0.5 ? 1.6 : 1.0;
		ground = mix(ground, ground * 0.5, clamp(line * contour_opacity * major, 0.0, 1.0));
	}

	vec3 color = mix(water_color.rgb, ground, land);

	// Thin coastline, constant on-screen thickness.
	float coast_line = 1.0 - smoothstep(0.6, 1.4, abs(h) / fw);
	color = mix(color, coastline_color.rgb, coast_line * coastline_opacity);

	if (show_territory) {
		int id;
		int unused;
		float edge;
		vote(owner_tex, ivec2(map_size), tile, true, WATER, id, unused, edge);
		if (id != WATER && id != 0) {
			vec3 nation = texelFetch(palette_tex, ivec2(id & 255, id >> 8), 0).rgb;

			// Distance to the coastline in tiles, so territories get an outline along the shore too.
			float coast_dist = h / max(length(vec2(tx1.r - tx0.r, ty1.r - ty0.r) * 0.5), 1e-4);

			// Borders never get thinner than ~1 pixel, so they don't flicker when zoomed out.
			float edge_aa = max(fwidth(edge), 1e-5);
			float bw = max(border_width, edge_aa * 1.2);
			float border = 1.0 - smoothstep(bw - edge_aa, bw + edge_aa, edge);
			float cbw = max(coast_border_width, tiles_per_px * 1.2);
			float coast_border = 1.0 - smoothstep(cbw - tiles_per_px, cbw + tiles_per_px, coast_dist);
			border = max(border, coast_border);

			vec3 fill = mix(ground, nation, territory_opacity);
			vec3 outline = nation * (1.0 - border_darken);
			vec3 owned = mix(fill, outline, border);
			color = mix(color, owned, land);
		}
	}

	COLOR = vec4(color, 1.0);
}
