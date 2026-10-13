shader_type canvas_item;

uniform sampler2D SCREEN_TEXTURE : hint_screen_texture, filter_linear_mipmap;

// Blur
uniform float blur_amount : hint_range(0.0, 5.0) = 1.5;

// Darken the blurred background a bit
uniform float darken_amount : hint_range(0.0, 1.0) = 0.75;

// Grain settings
uniform float noise_strength : hint_range(0.0, 1.0) = 0.07; // how strong the grain is
uniform float grain_scale    : hint_range(10.0, 400.0) = 150.0; // how big the grain is
uniform float noise_speed    : hint_range(0.0, 3.0) = 0.5; // 0 = static, >0 = shimmery

// Pseudo-random function
float rand(vec2 co) {
    return fract(sin(dot(co, vec2(12.9898, 78.233))) * 43758.5453123);
}

// Simple multi-octave noise for nicer randomness
float grain_noise(vec2 uv) {
    float n  = rand(uv * grain_scale);
    n       += 0.5 * rand(uv * grain_scale * 2.0);
    n       += 0.25 * rand(uv * grain_scale * 4.0);
    return n / (1.0 + 0.5 + 0.25); // normalize back to 0..1
}

void fragment() {
    // --- Blur the background ---
    vec3 col = textureLod(SCREEN_TEXTURE, SCREEN_UV, blur_amount).rgb;

    // --- Darken it ---
    col *= darken_amount;

    // --- Grain ---
    // Animate UV slightly over time so each frame is different
    vec2 uv = SCREEN_UV + vec2(TIME * noise_speed, TIME * 0.37 * noise_speed);
    float g = grain_noise(uv);          // 0..1
    float g2 = g * 2.0 - 1.0;           // -1..1

    // Add grain on top
    col += vec3(g2) * noise_strength;

    // Clamp to valid color range
    col = clamp(col, 0.0, 1.0);

    COLOR.rgb = col;
    // If you want to control opacity of the panel itself:
    // COLOR.a = 1.0; // or something like 0.9
}