shader_type canvas_item;

void vertex() {
	
}

// Horizontal wave shader
// Waves are oriented as horizontal bands (varying with Y) and move along the X axis
// A few layered sine waves produce a natural look; crests are highlighted to simulate foam.

uniform float time_scale = 0.25;
uniform float speed = 1;            // how fast the waves animate
uniform float frequency = 30.0;      // base frequency (bands per unit UV)
uniform float amplitude = 0.001;      // displacement amplitude in UV units
uniform float secondary_freq = 60.0; // second harmonic frequency
uniform float secondary_amp = 0.008; // second harmonic amplitude
uniform vec2 flow_dir = vec2(1.0, 0.0); // direction of displacement (horizontal)
uniform vec4 water_color = vec4(0.02, 0.12, 0.9, 1.0);
uniform float crest_sharpness = 6.0; // higher = thinner crests
uniform float foam_intensity = 0.1; // how strong the crest highlight is

// Noise controls for jittering crests
uniform float noise_scale = 6.0;       // spatial scale of the noise
uniform float noise_speed = 0.6;       // temporal speed multiplier for noise
uniform float noise_amp = 0.6;         // how strongly noise perturbs the wave phase
uniform float noise_displace = 0.008;  // how much noise displaces UV sampling

// --- small hash / value-noise / fbm implementation ---
float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	float a = hash(i);
	float b = hash(i + vec2(1.0, 0.0));
	float c = hash(i + vec2(0.0, 1.0));
	float d = hash(i + vec2(1.0, 1.0));
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
}

float fbm(vec2 p) {
	float v = 0.0;
	float amp = 0.5;
	for (int i = 0; i < 4; i++) {
		v += amp * noise(p);
		p *= 2.0;
		amp *= 0.5;
	}
	return v;
}

void fragment() {
	vec4 tex = texture(TEXTURE, UV);

	// use averaged luminance to detect white/ocean areas, use smoothstep for soft edges
	float lum = (tex.r + tex.g + tex.b) / 1.0;
	float mask = smoothstep(0.85, 0.95, lum);

	if (mask > 0.01) {
		// Primary horizontal wave depends on Y so crests are horizontal lines
		float phase = TIME * speed * time_scale;
		// base layered waves
		float w1 = sin(UV.y * frequency + phase);
		float w2 = 0.6 * sin(UV.y * secondary_freq + phase * 1.9 + UV.x * 1.2);
		float wave = w1 * 0.85 + w2 * 0.6;

		// sample procedural noise to perturb the wave phase and displacement
		float n = fbm(vec2(UV.x * noise_scale, UV.y * noise_scale + TIME * noise_speed));
		// center noise around zero
		n = (n - 0.5) * 2.0;

		// perturb wave phase so crests 'bounce' vertically and horizontally
		wave += n * noise_amp * 0.8;

		// Offset UV along flow direction to create refraction-like motion
		float offset = wave * amplitude;
		float offset2 = sin(UV.y * (frequency * 0.5) + phase * 0.5) * secondary_amp;
		// add noise-based displacement to sampling for a wobble effect on crests
		float noise_disp = n * noise_displace;
		vec2 sampleUV = clamp(UV + flow_dir * (offset + offset2 + noise_disp), vec2(0.0), vec2(1.0));

		// sample the texture with the displaced UV for subtle variation
		vec3 samp = texture(TEXTURE, sampleUV).rgb;

		// compute a crest/foam mask (peaky near wave crests)
		// crest calculation uses the perturbed wave so foam follows noisy crests
		float crest_raw = abs(sin(UV.y * frequency + phase + n * noise_amp));
		float crest = pow(smoothstep(0.5, 1.0, crest_raw), crest_sharpness);

		// combine base water color, sampled texture tint, and foam highlights
		vec3 base = water_color.rgb;
		vec3 colorFromTexture = mix(base, samp * 0.8 + base * 0.2, 0.35);
		vec3 foam = vec3(1.0) * crest * foam_intensity;

		// final color is blended with the mask so edges fade naturally
		vec3 finalColor = colorFromTexture + foam * mask;
		finalColor = mix(tex.rgb, finalColor, mask);

		COLOR = vec4(finalColor, 1.0);
	} else {
		COLOR = tex;
	}
}

//void light() {
//	// Called for every pixel for every light affecting the CanvasItem.
//	// Uncomment to replace the default light processing function with this one.
//}
