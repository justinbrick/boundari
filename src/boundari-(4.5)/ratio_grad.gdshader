shader_type canvas_item;

// --- Rect & border in *pixels* ---
uniform vec2 rect_size_px = vec2(300.0, 40.0); // set from script or inspector
uniform float border_thickness_px  : hint_range(0.0, 64.0)  = 2.0;

// Colors as full RGBA with color picker + hex
uniform vec4 border_color       : source_color = vec4(0.9, 0.9, 0.9, 1.0);

// --- Gradient colors (editable in Inspector) ---
uniform vec4 left_dark_color    : source_color = vec4(0.3, 0.0, 0.0, 1.0); // far left (dark red)
uniform vec4 left_light_color   : source_color = vec4(1.0, 0.3, 0.3, 1.0); // near line (lighter red)

uniform vec4 right_dark_color   : source_color = vec4(0.0, 0.3, 0.0, 1.0); // far right (dark green)
uniform vec4 right_light_color  : source_color = vec4(0.3, 1.0, 0.3, 1.0); // near line (lighter green);

// Position of the "slider line" in 0..1 (we'll sync this with the LineIndicator)
uniform float line_pos : hint_range(0.0, 1.0) = 0.5;

// Optional extra brightness around the line
uniform float center_light_strength : hint_range(0.0, 1.0) = 0.3;
uniform float center_light_width    : hint_range(0.0, 0.5) = 0.15;

// Overall opacity of the panel
uniform float panel_alpha : hint_range(0.0, 1.0) = 1.0;


void fragment() {
    // --- 1) Basic rect in pixel space ---
    // p = pixel coords inside this rect (0..width, 0..height)
    vec2 p = UV * rect_size_px;

    // Distance to each edge in pixels
    float dist_left   = p.x;
    float dist_right  = rect_size_px.x - p.x;
    float dist_top    = p.y;
    float dist_bottom = rect_size_px.y - p.y;

    // Smallest distance to any edge
    float dist_to_edge = min(min(dist_left, dist_right), min(dist_top, dist_bottom));

    // --- 2) Base gradient: red on left, green on right, darker at ends ---
    float x = clamp(UV.x, 0.0, 1.0);

    vec3 base_color;
    if (x <= line_pos) {
        // Left side: from dark red at x=0 to lighter red at x=line_pos
        float t = (line_pos > 0.0) ? (x / line_pos) : 0.0;
        base_color = mix(left_dark_color.rgb, left_light_color.rgb, t);
    } else {
        // Right side: from lighter green at x=line_pos to dark green at x=1
        float denom = 1.0 - line_pos;
        float t = (denom > 0.0) ? ((1.0 - x) / denom) : 0.0;
        base_color = mix(right_dark_color.rgb, right_light_color.rgb, t);
    }

    // --- 3) Extra brightness near the line (soft "glow" of color) ---
    float d = abs(x - line_pos);
    float light = 0.0;
    if (center_light_width > 0.0 && center_light_strength > 0.0) {
        float n = clamp(1.0 - d / center_light_width, 0.0, 1.0);
        light = n * center_light_strength;
    }
    vec3 gradient_color = base_color * (1.0 + light);

    // --- 4) Border: blend in border_color near the outer edge, in pixels ---
    float b_thick = max(border_thickness_px, 0.0001);
    // border_t = 1 at the edge, 0 once we're further than border_thickness_px inside
    float border_t = clamp(1.0 - dist_to_edge / b_thick, 0.0, 1.0);
    vec3 color_with_border = mix(gradient_color, border_color.rgb, border_t);

    // --- 5) Final color & alpha ---
    COLOR.rgb = color_with_border;
    COLOR.a   = panel_alpha;
}