shader_type spatial;
// Outer apron skirt ring around the inner turf plane (docs/football-grass.md §5.3). Deliberately
// near-free: one macro-noise fetch, no markings SDF, no detail normal, no sheen. This ring only
// exists to guarantee the ground reads as ground out to the 65 m / 21:9 corner envelope (~200 m).
// It receives shadows and never casts them.
render_mode cull_disabled;

// Must match PitchSurfaceBuilder's inner-mesh fade colour exactly, or the flush y = 0.0 seam shows.
uniform vec3 apron_color : source_color = vec3(0.158, 0.302, 0.161);
uniform vec3 apron_far_color : source_color = vec3(0.128, 0.224, 0.137);
uniform float roughness = 0.95;
// Distance over which the far colour takes over so the limit of the plane is not a flat wash.
uniform float far_near = 170.0;
uniform float far_far = 450.0;
// Large-scale variation so the surroundings are not a single flat wash (one fetch, cheap).
uniform sampler2D macro_noise : filter_linear_mipmap, repeat_enable;
uniform float macro_strength = 0.07;

varying vec3 world_pos;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    // VERTEX is the fragment position in view space; the camera looks down -Z, so -VERTEX.z is depth.
    float dist = -VERTEX.z;
    float macro = texture(macro_noise, world_pos.xz * (1.0 / 64.0)).r - 0.5;
    vec3 albedo = mix(apron_color, apron_far_color, smoothstep(far_near, far_far, dist));
    ALBEDO = albedo * (1.0 + macro * macro_strength * 2.0);
    ROUGHNESS = roughness;
}
