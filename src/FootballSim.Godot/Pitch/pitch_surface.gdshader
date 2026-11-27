shader_type spatial;

// Pitch ground surface â€” docs/football-grass.md Â§5.3.
//
// One opaque spatial shader shared by the inner turf + run-off plane. Everything is keyed off world
// XZ, never UVs: the mowing bands, the quarter-folded analytic markings SDF, the wear mask and the
// packed detail texture all read world position, so the material adapts to any configurable
// PitchDimensions without UV seams and the two ground meshes agree exactly at their shared edge.
//
// The ground never casts shadows (node CastShadow = Off) but receives them; there is deliberately no
// `shadows_disabled` render mode. Keep isotropic GGX: no ANISOTROPY/ANISOTROPY_FLOW/BACKLIGHT writes.

// ---------------------------------------------------------------------------------------------
// Geometry (set once per surface build, straight from PitchDimensions)
// ---------------------------------------------------------------------------------------------
uniform vec2 pitch_half_size = vec2(52.5, 34.0);
// (penalty area length, penalty area half width, goal area length, goal area half width)
uniform vec4 area_metrics = vec4(16.5, 20.16, 5.5, 9.16);
// (centre circle radius, penalty spot distance, corner arc radius, spot radius)
uniform vec4 arc_metrics = vec4(9.15, 11.0, 1.0, 0.22);
// Mowing band width: pitch.Length / (2 * round(pitch.Length / 11)) â€” an even band count centred on
// the origin so halfway and both goal lines always land on a band boundary.
uniform float stripe_width = 5.25;
uniform float line_width = 0.12;
// Mower travel / blade-lean axis in world XZ (bands run touchline to touchline, lean along Z).
uniform vec2 mow_lean_axis = vec2(0.0, 1.0);
// Inner mesh run-off beyond every line, and the width over which it fades into the apron colour.
uniform float run_off_width = 20.0;
uniform float apron_fade_width = 6.0;

// ---------------------------------------------------------------------------------------------
// Palette (sRGB source colours; the source_color hint converts them to linear for shading)
// ---------------------------------------------------------------------------------------------
uniform vec3 turf_light : source_color = vec3(0.182, 0.396, 0.153);
uniform vec3 turf_dark : source_color = vec3(0.172, 0.374, 0.145);
uniform vec3 paint_color : source_color = vec3(0.878, 0.878, 0.845);
uniform vec3 soil_color : source_color = vec3(0.290, 0.278, 0.192);
uniform vec3 apron_color : source_color = vec3(0.145, 0.263, 0.137);

// ---------------------------------------------------------------------------------------------
// Textures (all procedural, generated once by PitchTextureFactory / PitchWearTexture)
// ---------------------------------------------------------------------------------------------
// RG = turf detail normal XY (0.5 + 0.5*n), B = micro albedo luminance, A = micro AO.
uniform sampler2D detail_packed : filter_linear_mipmap, repeat_enable;
// R = macro turf variation, sampled at two world scales.
uniform sampler2D macro_noise : filter_linear_mipmap, repeat_enable;
// R = wear/scuff (0 pristine .. 1 worn), maps the pitch rectangle exactly, unmipped on purpose.
uniform sampler2D wear_map : filter_linear, repeat_disable;

// ---------------------------------------------------------------------------------------------
// Tuning (set per quality tier; Â§5.7)
// ---------------------------------------------------------------------------------------------
uniform int quality_level = 2;          // 0 low, 1 medium, 2 high
uniform float detail_strength = 1.0;
uniform float sheen_strength = 1.0;
uniform float detail_scale_near = 0.75; // metres per detail texture repeat (fine grain)
uniform float detail_scale_mid = 3.8;   // metres per detail texture repeat (tuft scale)
uniform vec2 detail_parity_offset = vec2(0.37, 0.61);
uniform float band_contrast = 0.08;     // albedo swing between mown bands
uniform float mow_tilt = 0.048;         // tangent normal tilt of the leaning blades (tan of ~3 deg)
uniform float roughness_base = 0.86;
uniform float roughness_detail_boost = 0.09;
uniform float wear_strength = 1.0;
uniform int markings_enabled = 1;
// G2: xz = ground projection of the ball shadow, w = ball height above y = 0 (< 0 disables).
uniform vec4 ball_pos_height = vec4(0.0, 0.0, 0.0, -1.0);

varying vec3 world_pos;

// PI is a Godot shading-language built-in constant.
const float LINE_AA_FLOOR = 0.35;

float sd_box(vec2 p, vec2 b) {
    vec2 d = abs(p) - b;
    return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0);
}

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// Distance to the nearest painted marking, using symmetry about both axes to fold the whole IFAB
// layout into one quadrant (~7 primitives, true 90 degree corners, no round caps).
float markings_distance(vec2 wp) {
    vec2 q = abs(wp);
    // Goal line + touchline perimeter.
    float d = abs(sd_box(q, pitch_half_size));
    // Halfway line, clipped to the pitch width.
    d = min(d, max(q.x, q.y - pitch_half_size.y));
    // Penalty area: front line + its two side lines (the goal-line side is the perimeter).
    d = min(d, abs(sd_box(q - vec2(pitch_half_size.x - area_metrics.x * 0.5, 0.0),
                          vec2(area_metrics.x * 0.5, area_metrics.y))));
    // Goal area.
    d = min(d, abs(sd_box(q - vec2(pitch_half_size.x - area_metrics.z * 0.5, 0.0),
                          vec2(area_metrics.z * 0.5, area_metrics.w))));
    // Centre circle.
    d = min(d, abs(length(q) - arc_metrics.x));
    // Penalty arc: only the part outside the penalty area.
    d = min(d, max(abs(length(q - vec2(pitch_half_size.x - arc_metrics.y, 0.0)) - arc_metrics.x),
                   q.x - (pitch_half_size.x - area_metrics.x)));
    // Corner arc: only the part inside the pitch.
    d = min(d, max(abs(length(q - pitch_half_size) - arc_metrics.z),
                   max(q.x - pitch_half_size.x, q.y - pitch_half_size.y)));
    return d;
}

void fragment() {
    vec2 wp = world_pos.xz;

    // -----------------------------------------------------------------------------------------
    // 1. Even-aligned mowing bands. A soft square wave (no floor/parity integer steps) so pans and
    //    grazing views cannot alias; feather is at least one pixel wide and widens at distance.
    // -----------------------------------------------------------------------------------------
    float u = wp.x / stripe_width;
    float sin_u = sin(PI * u);
    float feather = max(0.06 / stripe_width * PI, fwidth(u) * PI);
    float stripe = smoothstep(-feather, feather, sin_u);
    float band_sign = stripe * 2.0 - 1.0;
    // Zero-cost per-band decorrelation of the detail grain (no extra texture fetch): offsets the
    // sample by a different fraction of a tile in each band and blends at the mower seam.
    vec2 uv_nudge = detail_parity_offset * sin_u;

    // -----------------------------------------------------------------------------------------
    // 2. Macro variation (~30 m and ~5 m turf blotches), always on and cheap.
    // -----------------------------------------------------------------------------------------
    float macro_a = texture(macro_noise, wp * (1.0 / 64.0)).r;
    float macro_b = texture(macro_noise, wp * (1.0 / 9.6) + vec2(0.31, 0.17)).r;
    float macro = (macro_a - 0.5) * 1.25 + (macro_b - 0.5) * 0.75;

    // -----------------------------------------------------------------------------------------
    // 3. Packed detail: one RGBA8 fetch per scale (RG normal, B albedo, A AO). Fades out with the
    //    on-screen pixel footprint; the fade is paid back as extra roughness (Toksvig-style
    //    variance compensation) so flattened mips cannot sparkle.
    // -----------------------------------------------------------------------------------------
    float footprint = max(length(fwidth(wp)), 1e-4);
    float detail_fade = 1.0 - smoothstep(0.055, 0.40, footprint);
    detail_fade *= detail_strength;
    vec2 detail_slope = vec2(0.0);
    float micro_lum = 0.0;
    float micro_ao = 1.0;
    float detail_active = detail_fade > 0.001 ? 1.0 : 0.0;
    if (quality_level >= 1 && detail_active > 0.5) {
        vec4 d1 = texture(detail_packed, wp * (1.0 / detail_scale_near) + uv_nudge);
        detail_slope += (d1.rg * 2.0 - 1.0);
        micro_lum += d1.b - 0.5;
        micro_ao *= d1.a;
        if (quality_level >= 2) {
            vec4 d2 = texture(detail_packed, wp * (1.0 / detail_scale_mid) + vec2(0.5, 0.25));
            detail_slope += (d2.rg * 2.0 - 1.0) * 0.55;
            micro_lum += (d2.b - 0.5) * 0.8;
            micro_ao *= mix(1.0, d2.a, 0.6);
        }
        detail_slope *= detail_fade;
        micro_lum *= detail_fade;
        micro_ao = mix(1.0, micro_ao, detail_fade);
    }

    // -----------------------------------------------------------------------------------------
    // 4. Wear: static zone mask gated inside the pitch rectangle so edge texels never smear into
    //    the run-off.
    // -----------------------------------------------------------------------------------------
    vec2 half_pitch = pitch_half_size;
    float inside_x = 1.0 - smoothstep(half_pitch.x - 0.8, half_pitch.x, abs(wp.x));
    float inside_y = 1.0 - smoothstep(half_pitch.y - 0.8, half_pitch.y, abs(wp.y));
    float in_pitch = inside_x * inside_y;
    vec2 wear_uv = (wp + half_pitch) / (half_pitch * 2.0);
    float wear = texture(wear_map, wear_uv).r * in_pitch * wear_strength;

    // -----------------------------------------------------------------------------------------
    // 5. Painted markings with coverage-preserving AA: far touchlines stay legible instead of
    //    fading out when one pixel is wider than the line.
    // -----------------------------------------------------------------------------------------
    float paint_cov = 0.0;
    if (markings_enabled > 0) {
        float d = markings_distance(wp);
        float fw = max(fwidth(d), 1e-4);
        float half_w = line_width * 0.5;
        float eff_half = max(half_w, fw * 0.65);
        float aa = max(fw, 0.008);
        float cov_line = (1.0 - smoothstep(eff_half - aa, eff_half + aa, d))
                       * clamp(half_w / eff_half, LINE_AA_FLOOR, 1.0);
        // Filled spots share the same transition so they read as discs, not hollow rings.
        vec2 q = abs(wp);
        float spot_d = min(length(q) - arc_metrics.w,
                           length(q - vec2(half_pitch.x - arc_metrics.y, 0.0)) - arc_metrics.w);
        float spot_fw = max(fwidth(spot_d), 0.004);
        float cov_spot = 1.0 - smoothstep(-spot_fw * 1.5, spot_fw * 1.5, spot_d);
        paint_cov = max(cov_line, cov_spot);
    }

    // -----------------------------------------------------------------------------------------
    // 6. Albedo composition.
    // -----------------------------------------------------------------------------------------
    vec3 grass = mix(turf_dark, turf_light, stripe);
    grass *= 1.0 + macro * 0.11;
    // Subtle multi-scale hue drift: blotches shift slightly toward a yellow-green, and the near
    // grain modulates luminance by a couple of percent.
    grass += vec3(0.010, 0.012, -0.004) * macro * 0.5;
    grass *= 1.0 + micro_lum * 0.22;
    // Compacted, scuffed turf: darker, greyer, slightly broken toward soil.
    vec3 worn = mix(grass, soil_color * 0.62, wear * 0.42);
    worn *= 1.0 - wear * 0.20;
    grass = worn;

    // Painted lines: matte chalk, slightly scuffed by the wear mask.
    float paint = paint_cov * (1.0 - wear * 0.18);
    vec3 albedo = mix(grass, paint_color * (1.0 - wear * 0.12), paint);

    // Run-off: fade the last few metres of the inner mesh into the flat apron colour so the two
    // flush meshes meet without a visible border.
    float d_outer = max(abs(wp.x) - half_pitch.x, abs(wp.y) - half_pitch.y);
    float edge_t = smoothstep(run_off_width - apron_fade_width, run_off_width, d_outer);
    albedo = mix(albedo, apron_color, edge_t);

    // -----------------------------------------------------------------------------------------
    // 7. Analytic ball contact shadow (G2): readable aerial height, ~5 ALU ops.
    // -----------------------------------------------------------------------------------------
    if (ball_pos_height.w >= 0.0) {
        float h = ball_pos_height.w;
        float bd = length(wp - ball_pos_height.xy);
        float shadow_r = mix(0.26, 0.62, clamp(h / 5.0, 0.0, 1.0));
        float alpha = 0.55 * exp(-h * 0.35);
        float occl = 1.0 - alpha * (1.0 - smoothstep(shadow_r * 0.25, shadow_r, bd));
        albedo *= occl;
    }

    ALBEDO = albedo;

    // -----------------------------------------------------------------------------------------
    // 8. World-space stripe sheen + tangent-space blade tilt (Rev 3: never mix VIEW with a world
    //    axis). The per-band lean is folded straight into the shaded normal.
    // -----------------------------------------------------------------------------------------
    vec2 view_xz = normalize(CAMERA_POSITION_WORLD.xz - wp);
    vec2 band_lean = band_sign * mow_lean_axis;
    float sheen_align = dot(view_xz, band_lean);
    vec2 slope = band_lean * (mow_tilt * sheen_strength) + detail_slope * (1.0 - paint * 0.65);
    slope *= (1.0 - edge_t);
    vec3 world_normal = normalize(vec3(slope.x, 1.0, slope.y));
    NORMAL = normalize((VIEW_MATRIX * vec4(world_normal, 0.0)).xyz);

    // View-dependent luminance/roughness modulation: opposing bands catch sun + sky IBL differently,
    // so the pattern shifts subtly across the frame on a pan instead of being a flat albedo stripe.
    float sheen = sheen_align * sheen_strength;
    ALBEDO *= 1.0 + sheen * band_contrast * 0.30;

    // -----------------------------------------------------------------------------------------
    // 9. Material response.
    // -----------------------------------------------------------------------------------------
    float rough = roughness_base;
    rough += roughness_detail_boost * (1.0 - detail_fade);
    rough -= sheen * 0.030;
    rough = mix(rough, 0.92, paint);
    rough = mix(rough, 0.95, edge_t);
    ROUGHNESS = clamp(rough, 0.25, 1.0);

    AO = mix(1.0, micro_ao, 1.0 - paint * 0.5);
    AO *= mix(1.0, 0.86, wear * in_pitch);
    // Faint backlit velvet rim on the mown surface; zero on a flat ground under an overhead sun is
    // pointless, but the grazing sky term gives bands a little extra edge separation.
    RIM = 0.035 * sheen_strength;
    RIM_TINT = 0.35;
    SPECULAR = 0.5;
}

