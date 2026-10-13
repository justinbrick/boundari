shader_type canvas_item;
// NOTE: Set "Texture Filter = Nearest" on the MapDisplay Sprite2D node
// in the Godot Inspector to prevent bilinear blurring of the heightmap.

// Sobel kernel step — increase to pick up broader slope features
// at the cost of fine edge sharpness. 1.0 = single texel.
uniform float sobel_step : hint_range(0.5, 4.0) = 1.5;

// ── Thresholds (must match GDScript constants) ────────────────
uniform float ocean_threshold    : hint_range(0.0, 1.0) = 0.42;
uniform float hill_threshold     : hint_range(0.0, 1.0) = 0.20;
uniform float mountain_threshold : hint_range(0.0, 1.0) = 0.52;

// Biome split point and transition softness
uniform float biome_split    : hint_range(0.0, 1.0) = 0.50;
uniform float biome_softness : hint_range(0.0, 0.5) = 0.18;

// ── Relief ────────────────────────────────────────────────────
uniform float relief_strength     : hint_range(0.0, 1.0)   = 0.85;
uniform float relief_exaggeration : hint_range(1.0, 100.0) = 32.0;
uniform float light_angle_deg     : hint_range(0.0, 360.0) = 315.0;
uniform float shadow_floor        : hint_range(0.0, 0.5)   = 0.10;

// Per-terrain relief scaling — mountains dramatic, flats subtle
uniform float flat_relief_scale : hint_range(0.0, 2.0) = 0.35;
uniform float hill_relief_scale : hint_range(0.0, 2.0) = 0.90;
uniform float mtn_relief_scale  : hint_range(0.5, 4.0) = 2.20;

// ── Surface detail ────────────────────────────────────────────
uniform float grass_patch_scale : hint_range(1.0, 20.0) = 6.0;
uniform float desert_wisp_scale : hint_range(1.0, 20.0) = 7.0;

// ── HOI4-palette colors ───────────────────────────────────────
// Ocean
uniform vec4 COL_OCEAN_DEEP    : source_color = vec4(0.22, 0.32, 0.45, 1.0);
uniform vec4 COL_OCEAN_SHALLOW : source_color = vec4(0.32, 0.46, 0.58, 1.0);

// Grassland — muted olive/sage tones
uniform vec4 COL_GRASS_FLAT_LO : source_color = vec4(0.42, 0.50, 0.30, 1.0);
uniform vec4 COL_GRASS_FLAT_HI : source_color = vec4(0.55, 0.61, 0.38, 1.0);
uniform vec4 COL_GRASS_HILL_LO : source_color = vec4(0.38, 0.46, 0.27, 1.0);
uniform vec4 COL_GRASS_HILL_HI : source_color = vec4(0.50, 0.56, 0.34, 1.0);
uniform vec4 COL_GRASS_MTN_LO  : source_color = vec4(0.40, 0.38, 0.30, 1.0);
uniform vec4 COL_GRASS_MTN_HI  : source_color = vec4(0.62, 0.58, 0.48, 1.0);

// Desert — warm ochre/tan tones
uniform vec4 COL_DESE_FLAT_LO  : source_color = vec4(0.68, 0.58, 0.36, 1.0);
uniform vec4 COL_DESE_FLAT_HI  : source_color = vec4(0.78, 0.70, 0.50, 1.0);
uniform vec4 COL_DESE_HILL_LO  : source_color = vec4(0.60, 0.46, 0.28, 1.0);
uniform vec4 COL_DESE_HILL_HI  : source_color = vec4(0.75, 0.60, 0.38, 1.0);
uniform vec4 COL_DESE_MTN_LO   : source_color = vec4(0.50, 0.38, 0.24, 1.0);
uniform vec4 COL_DESE_MTN_HI   : source_color = vec4(0.68, 0.54, 0.36, 1.0);

// ── Dual texture layout ───────────────────────────────────────
// TEXTURE      = FORMAT_RF    — full float heightmap (R channel only)
// data_texture = FORMAT_RGBA8 — biome (R), patch noise (G)
uniform sampler2D data_texture : filter_nearest;

void fragment() {
	vec2 texel = TEXTURE_PIXEL_SIZE;

	// Full-precision height from RF texture
	float h = texture(TEXTURE, UV).r;

	// Biome and patch from 8-bit data texture
	vec4  data    = texture(data_texture, UV);
	float biome   = data.r;
	float patch_n = data.g;

	// ── Sobel on full-float heightmap ─────────────────────────
	// With FORMAT_RF neighbors are true float values —
	// no 8-bit stepping, no banding in the normal calculation.
	vec2 s = texel * sobel_step;
	float h_l  = texture(TEXTURE, UV + vec2(-1.0,  0.0) * s).r;
	float h_r  = texture(TEXTURE, UV + vec2( 1.0,  0.0) * s).r;
	float h_u  = texture(TEXTURE, UV + vec2( 0.0, -1.0) * s).r;
	float h_d  = texture(TEXTURE, UV + vec2( 0.0,  1.0) * s).r;
	float h_ul = texture(TEXTURE, UV + vec2(-1.0, -1.0) * s).r;
	float h_ur = texture(TEXTURE, UV + vec2( 1.0, -1.0) * s).r;
	float h_dl = texture(TEXTURE, UV + vec2(-1.0,  1.0) * s).r;
	float h_dr = texture(TEXTURE, UV + vec2( 1.0,  1.0) * s).r;

	float dzdx = (h_ur + 2.0*h_r + h_dr - h_ul - 2.0*h_l - h_dl) / 8.0;
	float dzdy = (h_dl + 2.0*h_d + h_dr - h_ul - 2.0*h_u - h_ur) / 8.0;

	// Land height normalised 0–1
	float land_h = clamp((h - ocean_threshold) / (1.0 - ocean_threshold), 0.0, 1.0);

	// Per-terrain relief scale — flats get subtle shading,
	// mountains get dramatic shadows
	float t_hill = smoothstep(hill_threshold     - 0.04, hill_threshold     + 0.04, land_h);
	float t_mtn  = smoothstep(mountain_threshold - 0.04, mountain_threshold + 0.04, land_h);
	float rscale = mix(flat_relief_scale, hill_relief_scale, t_hill);
	rscale       = mix(rscale,            mtn_relief_scale,  t_mtn);

	dzdx *= relief_exaggeration * rscale;
	dzdy *= relief_exaggeration * rscale;

	vec3 normal    = normalize(vec3(-dzdx, -dzdy, 1.0));
	float angle    = light_angle_deg * 3.14159265 / 180.0;
	vec3 light_dir = normalize(vec3(cos(angle), sin(angle), 1.0));
	float shade    = clamp(dot(normal, light_dir), 0.0, 1.0);
	shade = shade * (1.0 - shadow_floor) + shadow_floor;
	shade = pow(shade, 0.65);  // lift highlights slightly

	// ── Ocean color — depth gradient ──────────────────────────
	float ocean_t    = clamp(h / ocean_threshold, 0.0, 1.0);
	vec3 ocean_color = mix(COL_OCEAN_DEEP.rgb, COL_OCEAN_SHALLOW.rgb, ocean_t);

	// ── Land color — per terrain band, per biome ──────────────
	float flat_band = clamp(land_h / hill_threshold, 0.0, 1.0);
	float hill_band = clamp((land_h - hill_threshold) /
		(mountain_threshold - hill_threshold), 0.0, 1.0);
	float mtn_band  = clamp((land_h - mountain_threshold) /
		(1.0 - mountain_threshold), 0.0, 1.0);

	// Grassland base colors per band
	vec3 grass_flat = mix(COL_GRASS_FLAT_LO.rgb, COL_GRASS_FLAT_HI.rgb, flat_band);
	vec3 grass_hill = mix(COL_GRASS_HILL_LO.rgb, COL_GRASS_HILL_HI.rgb, hill_band);
	vec3 grass_mtn  = mix(COL_GRASS_MTN_LO.rgb,  COL_GRASS_MTN_HI.rgb,  mtn_band);

	// Grassland: blotchy dark spots using patch noise
	float grass_spot = smoothstep(
		0.5 - grass_patch_scale * 0.03,
		0.5 + grass_patch_scale * 0.03, patch_n);
	grass_flat = mix(grass_flat, grass_flat * 0.78, grass_spot * 0.55);
	grass_hill = mix(grass_hill, grass_hill * 0.80, grass_spot * 0.40);

	// Desert base colors per band
	vec3 dese_flat = mix(COL_DESE_FLAT_LO.rgb, COL_DESE_FLAT_HI.rgb, flat_band);
	vec3 dese_hill = mix(COL_DESE_HILL_LO.rgb, COL_DESE_HILL_HI.rgb, hill_band);
	vec3 dese_mtn  = mix(COL_DESE_MTN_LO.rgb,  COL_DESE_MTN_HI.rgb,  mtn_band);

	// Desert: reddish sandy wisps using patch noise
	float desert_wisp = smoothstep(
		0.5 - desert_wisp_scale * 0.03,
		0.5 + desert_wisp_scale * 0.03, patch_n);
	vec3 wisp_color = vec3(0.72, 0.38, 0.20);
	dese_flat = mix(dese_flat, wisp_color, desert_wisp * 0.30);
	dese_hill = mix(dese_hill, wisp_color, desert_wisp * 0.22);

	// Blend terrain bands together
	vec3 grass_land = mix(grass_flat, grass_hill, t_hill);
	grass_land      = mix(grass_land, grass_mtn,  t_mtn);
	vec3 dese_land  = mix(dese_flat,  dese_hill,  t_hill);
	dese_land       = mix(dese_land,  dese_mtn,   t_mtn);

	// ── Biome blend — border warp is pre-baked into biome channel ──
	float biome_t = smoothstep(
		biome_split - biome_softness,
		biome_split + biome_softness,
		biome);
	// Extra sharpening — snap to solid biome outside the strip
	biome_t = smoothstep(0.05, 0.95, biome_t);

	vec3 land_color = mix(grass_land, dese_land, biome_t);

	// Apply hillshading
	land_color = mix(land_color, land_color * shade, relief_strength);

	// ── Coast anti-alias ──────────────────────────────────────
	float e_sum = 0.0;
	for (int dy = -1; dy <= 1; dy++)
		for (int dx = -1; dx <= 1; dx++)
			e_sum += texture(TEXTURE, UV + vec2(float(dx), float(dy)) * texel).r;  // coast AA uses 1-texel, not sobel_step
	float e_coast = e_sum / 9.0;
	float fw      = fwidth(e_coast) * 2.5;
	float coast_t = smoothstep(ocean_threshold - fw, ocean_threshold + fw, e_coast);

	// Height replacement handles removed landmasses — coast AA is sufficient
	vec3 color = mix(ocean_color, land_color, coast_t);

	COLOR = vec4(color, 1.0);
}
