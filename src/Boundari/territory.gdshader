shader_type canvas_item;

// ### Uniforms (Shader Parameters) ###

// --- Textures ---
uniform sampler2D map_texture : source_color;
uniform sampler2D noise_texture : repeat_enable, filter_nearest;

// --- Colors ---
uniform vec4 ocean_deep : source_color = vec4(0.1, 0.3, 0.7, 1.0);
uniform vec4 ocean_shallow : source_color = vec4(0.2, 0.5, 1.0, 1.0);
uniform vec4 foam_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform vec4 land_color_tint : source_color = vec4(1.0, 1.0, 1.0, 1.0); // Tints the land map

// --- Waves ---
uniform float wave_scale : hint_range(10.0, 200.0) = 50.0;
uniform float wave_speed : hint_range(0.0, 1.0) = 0.1;

// --- Foam ---
uniform float foam_band_width : hint_range(0.0, 0.2) = 0.03;
uniform float foam_noise_scale : hint_range(10.0, 200.0) = 80.0;
uniform float foam_noise_speed : hint_range(0.0, 1.0) = 0.2;

// --- Shadow ---
uniform float shadow_distance : hint_range(0.0, 0.05) = 0.01;
uniform int shadow_steps : hint_range(1, 10) = 5;
uniform float shadow_strength : hint_range(0.0, 1.0) = 0.4;
uniform float wave_shadow_distort : hint_range(0.0, 0.01) = 0.002;



void fragment() {
	// ### 1. Time Calculation ###
	// A full 0.0 to 1.0 day cycle every 60 seconds.
	// 0.0 = 12:00 AM, 0.5 = 12:00 PM, 1.0 = 12:00 AM (next day)
	float day_cycle = mod(TIME, 60.0) / 60.0;

	// ### 2. Land & Water Masking ###
	// Sample the map. We use the red channel (r) to determine land/water.
	// 1.0 = water (white), 0.0 = land (black)
	vec4 map_sample = texture(map_texture, UV);
	float map_value = map_sample.r;

	// Create a clean 1.0/0.0 mask for land vs. water
	// land_val: 1.0 on land, 0.0 in water
	float land_val = 1.0 - map_value; 
	float land_mask = smoothstep(0.45, 0.55, land_val);
	float water_mask = 1.0 - land_mask;

	// ### 3. Water Ripples & Waves ###
	// Sample noise texture twice, moving in different directions for a ripple effect
	vec2 wave_uv1 = UV * wave_scale + vec2(TIME * wave_speed, TIME * wave_speed * 0.3);
	vec2 wave_uv2 = UV * wave_scale * 0.7 - vec2(TIME * wave_speed * -0.2, TIME * wave_speed);
	
	// Average the two noise samples
	float wave1 = texture(noise_texture, wave_uv1).r;
	float wave2 = texture(noise_texture, wave_uv2).r;
	float waves = (wave1 + wave2) * 0.5;

	// Mix water colors based on the wave height
	vec4 water_color = mix(ocean_deep, ocean_shallow, waves);

	// ### 4. Coastal Foam ###
	// Create a "band" just off the coast (where land_val is > 0.0 but < foam_band_width)
	float foam_band = smoothstep(0.0, foam_band_width, land_val) * (1.0 - smoothstep(foam_band_width, foam_band_width + 0.01, land_val));
	
	// Add noise to the foam to break it up
	vec2 foam_noise_uv = UV * foam_noise_scale + TIME * foam_noise_speed;
	float foam_noise = texture(noise_texture, foam_noise_uv).r;
	
	// Make foam jagged and influenced by waves
	float foam_mask = foam_band * smoothstep(0.3, 0.7, foam_noise + (waves - 0.5) * 0.5);

	// ### 5. Land Shadow ###
	// Calculate sun position
	// -0.25 offsets time so 6AM (0.25) is sunrise (angle 0)
	float light_angle = (day_cycle - 0.25) * 2.0 * PI;
	// light_dir: x=1 (East) at 6AM, x=0 at Noon, x=-1 (West) at 6PM
	vec2 light_dir = vec2(cos(light_angle), 0.3); // 0.3 gives a slight "north" cast
	
	// Sun altitude (0.0 at night, 1.0 at noon)
	float sun_altitude = max(0.0, sin(day_cycle * PI));

	// Calculate shadow by stepping away from the pixel towards the light
	float shadow = 0.0;
	if (sun_altitude > 0.0 && water_mask > 0.5) { // Only cast shadow on water during the day
		
		// Longer shadows at dawn/dusk, shorter at noon
		float current_shadow_dist = shadow_distance * (1.0 - sun_altitude + 0.2);
		vec2 step_vec = normalize(light_dir) * (current_shadow_dist / float(shadow_steps));
		
		// Distort shadow samples by the waves
		float wave_distort = (waves - 0.5) * wave_shadow_distort;
		vec2 distort_vec = vec2(wave_distort);

		// Raymarch for shadow
		for (int i = 1; i <= shadow_steps; i++) {
			vec2 sample_uv = UV - step_vec * float(i) + distort_vec;
			float land_sample = 1.0 - texture(map_texture, sample_uv).r; // 1.0 if land
			shadow += smoothstep(0.5, 1.0, land_sample); // Add to shadow if we hit land
		}
		shadow = clamp(shadow / float(shadow_steps), 0.0, 1.0);
	}
	
	// Final shadow strength
	shadow = shadow * sun_altitude * shadow_strength;

	// ### 6. Composite Final Color ###
	
	// 1. Start with the land color (tinted map texture)
	vec4 final_color = map_sample * land_color_tint;
	
	// 2. Mix in the water color where it's not land
	final_color = mix(water_color, final_color, land_mask);
	
	// 3. Add foam on top of the water
	final_color = mix(final_color, foam_color, foam_mask * water_mask);
	
	// 4. Apply shadow (darken) only to the water/foam
	final_color.rgb *= (1.0 - shadow * water_mask);

	// Set the final pixel color
	COLOR = final_color;
}