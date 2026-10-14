shader_type canvas_item;

// Draws the whole map: water, land, terrain and territories.
// Nothing here is a pixel image. Every screen pixel is computed from smooth
// fields plus procedural texture noise, so coastlines and borders stay curved
// and the ground stays detailed at any zoom.

// R = coast field (> 0 is land, coastline at 0), G = elevation (0..1).
// Linear filtering is required: the smooth shapes come from interpolating this.
uniform sampler2D terrain_tex : filter_linear, repeat_disable;
// R = light (x shade_range), G = vegetation, B = snow. See MapVisuals.cs.
uniform sampler2D look_tex : filter_linear, repeat_disable;
uniform float shade_range = 2.0;
// Owner id per tile, 16-bit in RG8. 0 = unclaimed land, 65535 = water.
uniform sampler2D owner_tex : filter_nearest, repeat_disable;
// 256x256 palette, one pixel per owner id.
uniform sampler2D palette_tex : filter_nearest, repeat_disable;

uniform vec2 map_size = vec2(2000.0, 1250.0);

group_uniforms territory;
uniform bool show_territory = true;
uniform float territory_opacity : hint_range(0.0, 1.0) = 0.55;
// Border thickness, roughly in tiles.
uniform float border_width : hint_range(0.0, 1.0) = 0.45;
uniform float coast_border_width : hint_range(0.0, 3.0) = 0.6;
uniform float border_darken : hint_range(0.0, 1.0) = 0.35;

group_uniforms water;
uniform vec4 deep_water : source_color = vec4(0.13, 0.22, 0.31, 1.0);
uniform vec4 shallow_water : source_color = vec4(0.23, 0.37, 0.45, 1.0);
uniform vec4 coastline_color : source_color = vec4(0.10, 0.14, 0.15, 1.0);

group_uniforms land;
uniform vec4 grass_color : source_color = vec4(0.38, 0.46, 0.25, 1.0);
uniform vec4 grass_alt_color : source_color = vec4(0.46, 0.50, 0.29, 1.0);
uniform vec4 forest_color : source_color = vec4(0.25, 0.33, 0.18, 1.0);
uniform vec4 dry_color : source_color = vec4(0.71, 0.62, 0.44, 1.0);
uniform vec4 highland_color : source_color = vec4(0.52, 0.49, 0.34, 1.0);
uniform vec4 rock_color : source_color = vec4(0.40, 0.38, 0.35, 1.0);
uniform vec4 snow_color : source_color = vec4(0.84, 0.86, 0.89, 1.0);
uniform vec4 snow_shadow_color : source_color = vec4(0.55, 0.62, 0.76, 1.0);
// How strongly the procedural ground texture shows.
uniform float texture_strength : hint_range(0.0, 2.0) = 1.0;

const int WATER = 65535;

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

// Tap positions/weights for a smooth (bicubic B-spline) sample from 4 bilinear taps.
void bspline_taps(vec2 tile, out vec2 h0, out vec2 h1, out vec2 g0, out vec2 g1) {
	vec2 p = tile - 0.5;
	vec2 i = floor(p);
	vec2 f = p - i;
	vec2 f2 = f * f;
	vec2 f3 = f2 * f;
	vec2 w0 = (1.0 - 3.0 * f + 3.0 * f2 - f3) / 6.0;
	vec2 w1 = (4.0 - 6.0 * f2 + 3.0 * f3) / 6.0;
	vec2 w2 = (1.0 + 3.0 * f + 3.0 * f2 - 3.0 * f3) / 6.0;
	vec2 w3 = f3 / 6.0;
	g0 = w0 + w1;
	g1 = w2 + w3;
	h0 = (i - 0.5 + w1 / g0) / map_size;
	h1 = (i + 1.5 + w3 / g1) / map_size;
}

vec2 terrain_at(vec2 tile) {
	vec2 h0, h1, g0, g1;
	bspline_taps(tile, h0, h1, g0, g1);
	vec2 a = texture(terrain_tex, vec2(h0.x, h0.y)).rg;
	vec2 b = texture(terrain_tex, vec2(h1.x, h0.y)).rg;
	vec2 c = texture(terrain_tex, vec2(h0.x, h1.y)).rg;
	vec2 d = texture(terrain_tex, vec2(h1.x, h1.y)).rg;
	return g0.y * (g0.x * a + g1.x * b) + g1.y * (g0.x * c + g1.x * d);
}

vec3 look_at(vec2 tile) {
	vec2 h0, h1, g0, g1;
	bspline_taps(tile, h0, h1, g0, g1);
	vec3 a = texture(look_tex, vec2(h0.x, h0.y)).rgb;
	vec3 b = texture(look_tex, vec2(h1.x, h0.y)).rgb;
	vec3 c = texture(look_tex, vec2(h0.x, h1.y)).rgb;
	vec3 d = texture(look_tex, vec2(h1.x, h1.y)).rgb;
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

// ---------------------------------------------------------------------------
//  Procedural texture
// ---------------------------------------------------------------------------

float hash(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

vec2 gradient(vec2 cell) {
	float a = hash(cell) * 6.2831853;
	return vec2(cos(a), sin(a));
}

// Gradient noise in roughly [0, 1]. Unlike value noise it has no blocky,
// grid-aligned artefacts.
float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = p - i;
	vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	float a = dot(gradient(i), f);
	float b = dot(gradient(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0));
	float c = dot(gradient(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0));
	float d = dot(gradient(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0));
	return clamp(0.5 + 0.9 * mix(mix(a, b, u.x), mix(c, d, u.x), u.y), 0.0, 1.0);
}

// One layer of texture detail in [-0.5, 0.5], `period` in tiles. Fades out as
// its features shrink below a couple of screen pixels, so it never shimmers.
float detail(vec2 tile, float period, float tiles_per_px) {
	float visible = 1.0 - smoothstep(0.2, 0.5, tiles_per_px / period);
	return (value_noise(tile / period) - 0.5) * visible;
}

vec3 ground_color(vec2 tile, float elev, float veg, float tiles_per_px) {
	float ts = texture_strength;

	// Grassland: regional hue shifts plus a patchwork of fields and meadows.
	float region = 0.65 * value_noise(tile / 160.0) + 0.35 * value_noise(tile / 40.0);
	vec3 col = mix(grass_color.rgb, grass_alt_color.rgb, smoothstep(0.3, 0.75, region));
	vec2 rot = mat2(vec2(0.87, 0.5), vec2(-0.5, 0.87)) * tile;
	float field_fade = 1.0 - smoothstep(0.15, 0.45, tiles_per_px);
	float fields = (hash(floor(rot / vec2(1.7, 1.1))) - 0.5) * field_fade;
	col *= 1.0 + ts * (0.07 * fields + 0.16 * detail(tile, 3.0, tiles_per_px)
		+ 0.12 * detail(tile, 10.0, tiles_per_px) + 0.07 * detail(tile, 0.6, tiles_per_px));

	// Forest: organic patches with a bumpy canopy texture.
	float f = veg + 0.22 * (value_noise(tile / 5.0) - 0.5) + 0.1 * (value_noise(tile / 1.3) - 0.5);
	float forest = smoothstep(0.62, 0.8, f);
	vec3 canopy = forest_color.rgb * (1.0 + ts * (0.45 * detail(tile, 0.3, tiles_per_px)
		+ 0.2 * detail(tile, 1.0, tiles_per_px) + 0.1 * detail(tile, 8.0, tiles_per_px)));
	col = mix(col, canopy, forest);

	// Dry land and steppe.
	float dry = smoothstep(0.38, 0.12, veg + 0.06 * (value_noise(tile / 6.0) - 0.5));
	vec3 sand = dry_color.rgb * (1.0 + ts * (0.1 * detail(tile, 4.0, tiles_per_px)
		+ 0.05 * detail(tile, 0.5, tiles_per_px)));
	col = mix(col, sand, dry);

	// Highlands turn olive-brown, mountains turn to rock.
	float high = smoothstep(0.6, 0.84, elev + 0.04 * (value_noise(tile / 3.0) - 0.5));
	col = mix(col, highland_color.rgb * (1.0 + 0.1 * ts * detail(tile, 1.5, tiles_per_px)), high * 0.55);
	float rock = smoothstep(0.78, 0.87, elev + 0.06 * (value_noise(tile / 2.0) - 0.5));
	vec3 stone = rock_color.rgb * (1.0 + ts * (0.25 * detail(tile, 0.7, tiles_per_px)
		+ 0.1 * detail(tile, 2.5, tiles_per_px)));
	col = mix(col, stone, rock);
	return col;
}

// ---------------------------------------------------------------------------

void fragment() {
	vec2 tile = UV * map_size;
	float tiles_per_px = max(length(dFdx(tile)), length(dFdy(tile)));

	vec2 t = terrain_at(tile);
	float h = t.r;
	float elev = t.g;
	vec3 look = look_at(tile);
	float shade = look.r * shade_range;
	float veg = look.g;
	float snow = look.b;

	// Anti-aliased land mask: about one screen pixel wide at every zoom level.
	float fw = max(fwidth(h), 1e-6);
	float land = smoothstep(-0.5 * fw, 0.5 * fw, h);

	// Water: lighter near the coast, darker in deep ocean, faint ripples up close.
	float depth = smoothstep(0.0, 0.07, -h);
	vec3 water = mix(shallow_water.rgb, deep_water.rgb, depth);
	water *= 1.0 + 0.06 * (value_noise(tile / 60.0) - 0.5) + 0.05 * detail(tile, 1.2, tiles_per_px);

	// Land: textured ground, lit by the precomputed sun and shadows.
	vec3 ground = ground_color(tile, elev, veg, tiles_per_px);
	float snow_edge = smoothstep(0.25, 0.7, snow + 0.3 * (value_noise(tile / 0.6) - 0.5) + 0.15 * (value_noise(tile / 2.5) - 0.5));
	vec3 snow_tex = snow_color.rgb * (1.0 + texture_strength * (0.08 * detail(tile, 0.5, tiles_per_px)
		+ 0.06 * detail(tile, 2.0, tiles_per_px)));
	ground = mix(ground, snow_tex, snow_edge);
	vec3 lit = ground * shade;
	// Shadowed snow turns blue-grey instead of just darker, like real snowfields.
	lit = mix(lit, snow_shadow_color.rgb * min(shade, 1.0) * 1.1, snow_edge * (1.0 - smoothstep(0.55, 1.0, shade)));

	vec3 color = mix(water, lit, land);

	// Thin dark coastline, constant on-screen thickness.
	float coast_line = 1.0 - smoothstep(0.6, 1.4, abs(h) / fw);
	color = mix(color, coastline_color.rgb, coast_line * 0.5);

	if (show_territory) {
		int id;
		float edge;
		territory_at(tile, id, edge);
		if (id != WATER && id != 0) {
			vec3 nation = texelFetch(palette_tex, ivec2(id & 255, id >> 8), 0).rgb;

			// Distance to the coastline in tiles, so territories get an outline along the shore too.
			float hx = terrain_at(tile + vec2(1.0, 0.0)).r - terrain_at(tile - vec2(1.0, 0.0)).r;
			float hy = terrain_at(tile + vec2(0.0, 1.0)).r - terrain_at(tile - vec2(0.0, 1.0)).r;
			float coast_dist = h / max(length(vec2(hx, hy) * 0.5), 1e-4);

			// Borders never get thinner than ~1 pixel, so they don't flicker when zoomed out.
			float edge_aa = max(fwidth(edge), 1e-5);
			float bw = max(border_width, edge_aa * 1.2);
			float border = 1.0 - smoothstep(bw - edge_aa, bw + edge_aa, edge);
			float cbw = max(coast_border_width, tiles_per_px * 1.2);
			float coast_border = 1.0 - smoothstep(cbw - tiles_per_px, cbw + tiles_per_px, coast_dist);
			border = max(border, coast_border);

			// Tint the lit ground, so terrain and shadows still show through the nation colour.
			vec3 fill = mix(lit, nation * shade, territory_opacity);
			vec3 outline = nation * (1.0 - border_darken);
			vec3 owned = mix(fill, outline, border);
			color = mix(color, owned, land);
		}
	}

	COLOR = vec4(color, 1.0);
}
