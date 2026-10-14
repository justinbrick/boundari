shader_type canvas_item;

// Draws the whole map: water, land, terrain shading and territories.
// Nothing here is a pixel image. Every screen pixel is computed from smooth
// fields, so coastlines and borders stay crisp and curved at any zoom.

// R = coast field (> 0 is land, coastline at 0), G = elevation (0..1).
// Linear filtering is required: the smooth shapes come from interpolating this.
uniform sampler2D terrain_tex : filter_linear, repeat_disable;
// Owner id per tile, 16-bit in RG8. 0 = unclaimed land, 65535 = water.
uniform sampler2D owner_tex : filter_nearest, repeat_disable;
// 256x256 palette, one pixel per owner id.
uniform sampler2D palette_tex : filter_nearest, repeat_disable;

uniform vec2 map_size = vec2(2000.0, 1250.0);

group_uniforms territory;
uniform bool show_territory = true;
uniform float territory_opacity : hint_range(0.0, 1.0) = 0.62;
// Border thickness, roughly in tiles.
uniform float border_width : hint_range(0.0, 1.0) = 0.45;
uniform float coast_border_width : hint_range(0.0, 3.0) = 0.6;
uniform float border_darken : hint_range(0.0, 1.0) = 0.35;

group_uniforms terrain;
uniform vec4 deep_water : source_color = vec4(0.231, 0.408, 0.616, 1.0);
uniform vec4 shallow_water : source_color = vec4(0.400, 0.612, 0.792, 1.0);
uniform vec4 coastline_color : source_color = vec4(0.18, 0.30, 0.42, 1.0);
uniform vec4 plains_color : source_color = vec4(0.659, 0.757, 0.494, 1.0);
uniform vec4 highlands_color : source_color = vec4(0.776, 0.722, 0.537, 1.0);
uniform vec4 mountains_color : source_color = vec4(0.612, 0.580, 0.533, 1.0);
uniform vec4 peaks_color : source_color = vec4(0.835, 0.827, 0.804, 1.0);
// Must match MapGenSettings.PlainsShare and PlainsShare + HighlandsShare.
uniform float plains_end = 0.55;
uniform float highlands_end = 0.85;
uniform float relief_strength : hint_range(0.0, 40.0) = 9.0;

const int WATER = 65535;

// Cubic B-spline weights for the 4 texels around a sample point.
void bspline_weights(float f, out float w[4]) {
	float f2 = f * f;
	float f3 = f2 * f;
	w[0] = (1.0 - 3.0 * f + 3.0 * f2 - f3) / 6.0;
	w[1] = (4.0 - 6.0 * f2 + 3.0 * f3) / 6.0;
	w[2] = (1.0 + 3.0 * f + 3.0 * f2 - 3.0 * f3) / 6.0;
	w[3] = f3 / 6.0;
}

// Smooth (bicubic B-spline) terrain sample using 4 hardware-bilinear taps.
// `tile` is a position in tile units (0..map_size).
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

int owner_at(ivec2 c) {
	c = clamp(c, ivec2(0), ivec2(map_size) - 1);
	vec2 rg = texelFetch(owner_tex, c, 0).rg;
	return int(rg.r * 255.0 + 0.5) + int(rg.g * 255.0 + 0.5) * 256;
}

// Smooth territory lookup. Each nearby tile "votes" for its owner with a
// B-spline weight; the winner owns this pixel. `edge` is how far the winner
// leads the runner-up: 0 exactly on a border, 1 deep inside a territory.
void territory_at(vec2 tile, out int best_id, out float edge) {
	vec2 p = tile - 0.5;
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
			int id = owner_at(base + ivec2(i - 1, j - 1));
			if (id == WATER) {
				continue; // water doesn't compete, so territory reaches the coastline
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

	best_id = WATER;
	edge = 1.0;
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

vec3 land_color(float e) {
	vec3 col = mix(plains_color.rgb * 0.94, plains_color.rgb * 1.04, smoothstep(0.0, plains_end, e));
	col = mix(col, highlands_color.rgb, smoothstep(plains_end - 0.03, plains_end + 0.03, e));
	col = mix(col, mountains_color.rgb, smoothstep(highlands_end - 0.02, highlands_end + 0.02, e));
	col = mix(col, peaks_color.rgb, smoothstep(0.97, 0.995, e));
	return col;
}

void fragment() {
	vec2 tile = UV * map_size;
	vec2 t = terrain_at(tile);
	float h = t.r;
	float elev = t.g;

	// Neighbouring samples give slope (for hill shading) and coast distance.
	vec2 tx0 = terrain_at(tile - vec2(1.0, 0.0));
	vec2 tx1 = terrain_at(tile + vec2(1.0, 0.0));
	vec2 ty0 = terrain_at(tile - vec2(0.0, 1.0));
	vec2 ty1 = terrain_at(tile + vec2(0.0, 1.0));

	// Anti-aliased land mask: about one screen pixel wide at every zoom level.
	float fw = max(fwidth(h), 1e-6);
	float land = smoothstep(-0.5 * fw, 0.5 * fw, h);

	// Water: lighter near the coast, darker in deep ocean.
	float depth = smoothstep(0.0, 0.07, -h);
	vec3 water = mix(shallow_water.rgb, deep_water.rgb, depth);

	// Land: elevation bands plus soft hill shading lit from the north-west.
	vec3 ground = land_color(elev);
	vec2 slope = vec2(tx1.g - tx0.g, ty1.g - ty0.g) * 0.5;
	// Gentle shading in lowlands, full shading in the mountains.
	float relief = relief_strength * mix(0.3, 1.0, smoothstep(0.5, 0.9, elev));
	vec3 normal = normalize(vec3(-slope * relief, 1.0));
	vec3 light_dir = normalize(vec3(-1.0, -1.0, 1.6));
	float shade = dot(normal, light_dir) / light_dir.z;
	ground *= clamp(mix(1.0, shade, 0.7), 0.6, 1.25);

	vec3 color = mix(water, ground, land);

	// Thin coastline outline, constant on-screen thickness.
	float coast_line = 1.0 - smoothstep(0.6, 1.4, abs(h) / fw);
	color = mix(color, coastline_color.rgb, coast_line * 0.45);

	if (show_territory) {
		int id;
		float edge;
		territory_at(tile, id, edge);
		if (id != WATER && id != 0) {
			vec3 nation = texelFetch(palette_tex, ivec2(id & 255, id >> 8), 0).rgb;

			// Distance to the coastline in tiles, so territories get an outline along the shore too.
			float h_slope = length(vec2(tx1.r - tx0.r, ty1.r - ty0.r) * 0.5);
			float coast_dist = h / max(h_slope, 1e-4);
			float tiles_per_px = length(dFdx(tile));

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
