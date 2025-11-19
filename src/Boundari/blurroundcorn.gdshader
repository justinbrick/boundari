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

// Rounded corners IN PIXELS
uniform float corner_radius_px : hint_range(0.0, 200.0) = 24.0;
uniform float corner_smooth_px : hint_range(0.0, 40.0) = 6.0;

// Size of this ColorRect in pixels (set this to the bar's actual size)
uniform vec2 rect_size = vec2(400.0, 80.0);

// Overall opacity of this panel
uniform float panel_alpha : hint_range(0.0, 1.0) = 1.0;


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

// Proper rounded-rectangle mask in *pixel* space
float rounded_rect_mask(vec2 uv, vec2 size, float radius, float smoothness) {
    // Clamp radius so it never exceeds half the smallest dimension
    float max_r = 0.5 * min(size.x, size.y) - smoothness;
    radius = clamp(radius, 0.0, max_r);

    // Convert UV (0..1) to pixel coords centered at origin
    vec2 p = uv * size - size * 0.5;
    // Inner box half-size (minus radius)
    vec2 b = size * 0.5 - vec2(radius);

    vec2 d = abs(p) - b;
    float dist = length(max(d, vec2(0.0))) - radius;

    // 1.0 inside, 0.0 outside, smooth edge around dist = 0
    return 1.0 - smoothstep(-smoothness, smoothness, dist);
}

void fragment() {
    // --- Blur the background ---
    vec3 col = textureLod(SCREEN_TEXTURE, SCREEN_UV, blur_amount).rgb;

    // --- Darken it ---
    col *= darken_amount;

    // --- Grain ---
    vec2 uv = SCREEN_UV + vec2(TIME * noise_speed, TIME * 0.37 * noise_speed);
    float g = grain_noise(uv);          // 0..1
    float g2 = g * 2.0 - 1.0;           // -1..1
    col += vec3(g2) * noise_strength;

    col = clamp(col, 0.0, 1.0);

    // --- Rounded corners in pixel space ---
    float mask = rounded_rect_mask(UV, rect_size, corner_radius_px, corner_smooth_px);

    COLOR.rgb = col;
    COLOR.a   = panel_alpha * mask;
}