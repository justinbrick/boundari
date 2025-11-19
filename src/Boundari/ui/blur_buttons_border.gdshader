shader_type canvas_item;

// Reads the screen (the game) copied by BackBufferCopy
uniform sampler2D SCREEN_TEXTURE : hint_screen_texture, filter_linear_mipmap;

// --- Blur settings ---
uniform float blur_amount : hint_range(0.0, 5.0) = 1.5;

// --- Darken settings ---
// 1.0 = no darkening, 0.0 = full black
uniform float darken_amount : hint_range(0.0, 1.0) = 0.80;

// --- Grain / noise settings ---
uniform float noise_strength : hint_range(0.0, 1.0) = 0.06;  // how visible the grain is
uniform float grain_scale    : hint_range(10.0, 400.0) = 160.0; // size of grain
uniform float noise_speed    : hint_range(0.0, 3.0) = 0.4; // 0 = static, >0 = shimmery

// Optional tint over the blurred area (set to vec3(1.0) for no tint)
uniform vec3 tint_color = vec3(0.95, 0.95, 1.0);
uniform float tint_strength : hint_range(0.0, 1.0) = 0.20;

// Optional overall alpha of the button "glass"
uniform float panel_alpha : hint_range(0.0, 1.0) = 1.0;

// --- Border settings ---
uniform vec4  border_color = vec4(1.0, 1.0, 1.0, 0.9); // rgba, alpha = border strength
uniform float border_thickness : hint_range(0.0, 0.5) = 0.03; // in UV space
uniform float border_softness  : hint_range(0.0, 0.3) = 0.02; // how soft the edge is


// -------- noise helpers --------

float rand(vec2 co) {
    return fract(sin(dot(co, vec2(12.9898, 78.233))) * 43758.5453123);
}

// Multi-octave noise so it looks more organic and less grid-like
float grain_noise(vec2 uv) {
    float n  = rand(uv * grain_scale);
    n       += 0.5 * rand(uv * grain_scale * 2.0);
    n       += 0.25 * rand(uv * grain_scale * 4.0);
    return n / (1.0 + 0.5 + 0.25); // normalize back to 0..1
}


void fragment() {
    // --- 1) Blur the game behind this button ---
    vec3 col = textureLod(SCREEN_TEXTURE, SCREEN_UV, blur_amount).rgb;

    // --- 2) Darken it a bit ---
    col *= darken_amount;

    // --- 3) Apply tint (optional) ---
    col = mix(col, tint_color, tint_strength);

    // --- 4) Add grain / noise ---
    vec2 uv = SCREEN_UV + vec2(TIME * noise_speed, TIME * 0.37 * noise_speed);
    float g = grain_noise(uv);   // 0..1
    float g2 = g * 2.0 - 1.0;    // -1..1
    col += vec3(g2) * noise_strength;

    col = clamp(col, 0.0, 1.0);

    // --- 5) Border in UV space ---
    // Distance to the nearest edge (0 at edge, ~0.5 at center)
    float edge_dist = min(min(UV.x, UV.y), min(1.0 - UV.x, 1.0 - UV.y));

    // inner = 0 at the very edge, 1 in the interior
    float inner = smoothstep(border_thickness, border_thickness + border_softness, edge_dist);

    // border_mask = 1 at border, 0 in the interior
    float border_mask = 1.0 - inner;

    // Mix border color over the blurred content, using border_color.a as strength
    col = mix(col, border_color.rgb, border_color.a * border_mask);

    COLOR = vec4(col, panel_alpha);
}