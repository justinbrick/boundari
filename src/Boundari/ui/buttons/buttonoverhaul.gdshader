shader_type canvas_item;

uniform sampler2D SCREEN_TEXTURE : hint_screen_texture, filter_linear_mipmap;

// ---- Blur / darken base ----
uniform float blur_amount   : hint_range(0.0, 5.0) = 1.5;
uniform float darken_amount : hint_range(0.0, 1.0) = 0.75;

// ---- Grain settings ----
uniform float noise_strength : hint_range(0.0, 1.0) = 0.07;     // how strong the grain is
uniform float grain_scale    : hint_range(10.0, 400.0) = 150.0; // grain size
uniform float noise_speed    : hint_range(0.0, 3.0) = 0.5;      // 0 = static, >0 = shimmery

// ---- Hover / press state (set from script) ----
uniform float hover_amount   : hint_range(0.0, 1.0) = 0.0; // 0 = none, 1 = full hover
uniform float pressed_amount : hint_range(0.0, 1.0) = 0.0; // 0 = not pressed, 1 = pressed

// ---- Optional tint over the blurred area ----
uniform vec3  tint_color    = vec3(1.0, 1.0, 1.0);  // 1,1,1 = no tint
uniform float tint_strength : hint_range(0.0, 1.0) = 0.0;

// ---- Overall alpha of the button "glass" ----
uniform float panel_alpha : hint_range(0.0, 1.0) = 1.0;


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

    // --- 3) Apply optional tint ---
    col = mix(col, tint_color, tint_strength);

    // --- 4) Add grain / noise ---
    vec2 uv_noise = SCREEN_UV + vec2(TIME * noise_speed, TIME * 0.37 * noise_speed);
    float g  = grain_noise(uv_noise);   // 0..1
    float g2 = g * 2.0 - 1.0;           // -1..1
    col += vec3(g2) * noise_strength;

    col = clamp(col, 0.0, 1.0);

    // --- 5) Hover / pressed brightness tweaks ---
    float brightness = 1.0 + hover_amount * 0.20 - pressed_amount * 0.25;
    col *= brightness;
    col = clamp(col, 0.0, 1.0);

    // --- 6) Shape + stroke color from SVG texture ---
    // TEXTURE is whatever you assigned to the TextureRect's Texture (your SVG)
    vec4 shape_tex   = texture(TEXTURE, UV);
    float shape_alpha = shape_tex.a;

    // Optional: soften edges if needed
    // shape_alpha = smoothstep(0.05, 0.95, shape_alpha);

    // How strongly to show the SVG's own color (e.g. white stroke)
    float stroke_mix = 1.0; // 0 = ignore SVG color, 1 = full SVG stroke

    // Start from the blurred color
    vec3 final_rgb = col;

    // Add SVG stroke color on top, without replacing the blur
    final_rgb += shape_tex.rgb * (stroke_mix * shape_alpha);
    final_rgb = clamp(final_rgb, 0.0, 1.0);

    // Final alpha: only visible where SVG alpha exists, scaled by panel_alpha
    float final_alpha = panel_alpha * shape_alpha;

    COLOR = vec4(final_rgb, final_alpha);
}