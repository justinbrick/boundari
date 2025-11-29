shader_type canvas_item;

uniform sampler2D SCREEN_TEXTURE : hint_screen_texture, filter_linear_mipmap;

// Blur
// Higher max so you can push it further in the Inspector.
uniform float blur_amount : hint_range(0.0, 10.0) = 2.5;

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
    // -------- STRONGER BLUR --------
    // Use blur_amount to control both LOD and sample radius.
    // Feel free to tweak the multipliers if it's too strong/weak.
    float lod     = blur_amount * 0.6;          // how deep into mip chain we go
    float radius  = blur_amount * 0.004;        // how far we sample around the pixel

    // 3x3 box blur around the pixel, using that LOD
    vec3 col = vec3(0.0);
    float weight = 1.0 / 9.0;

    for (int x = -1; x <= 1; x++) {
        for (int y = -1; y <= 1; y++) {
            vec2 offset = vec2(float(x), float(y)) * radius;
            col += textureLod(SCREEN_TEXTURE, SCREEN_UV + offset, lod).rgb * weight;
        }
    }

    // -------- DARKEN --------
    col *= darken_amount;

    // -------- GRAIN --------
    // Animate UV slightly over time so each frame is different
    vec2 uv = SCREEN_UV + vec2(TIME * noise_speed, TIME * 0.37 * noise_speed);
    float g  = grain_noise(uv);       // 0..1
    float g2 = g * 2.0 - 1.0;         // -1..1

    col += vec3(g2) * noise_strength;
    col  = clamp(col, 0.0, 1.0);

    COLOR.rgb = col;
    // If you want to control opacity of the panel itself:
    // COLOR.a = 1.0; // or e.g. 0.9
}