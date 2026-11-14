using System;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Physics;

/// <summary>Launch velocity and spin of one struck ball.</summary>
public readonly record struct KickLaunch(Vector3 Velocity, Vector3 Spin);

/// <summary>
/// The execution layer's launch parameters for a pass-shaped kick, in one place (A5.5).
///
/// These six numbers per intent used to live inside <c>FootballerExecutionSystem</c> while two AI models
/// duplicated them: <see cref="PassPlanner"/> re-declared the ground-pass speed with a "mirrors the kick
/// model" comment, and <see cref="AI.PassSuccessModel"/> re-declared the raised band and then applied a flat
/// 15 % shortfall because it could not reproduce the drag and spin. The duplication is exactly the drift
/// risk the A5.5 instruction warns about ("do not assess an airborne delivery using ground-pass flight
/// times"), so the AI's airborne prediction is now calibrated from this type, and the execution calls it.
///
/// The band values, the clamps and the spin axes are moved verbatim from the execution switch; the kick
/// recovery timers stay there because they are body state, not launch geometry.
/// </summary>
public static class KickLaunchModel
{
    /// <summary>Power the execution layer actually strikes at (the command's power is clamped to this band).</summary>
    public const float MinPower = 0.15f;

    /// <summary>Highest power the execution layer strikes at.</summary>
    public const float MaxPower = 1.0f;

    /// <summary>Ball height at a kick: the resting centre height is the radius.</summary>
    public const float LaunchHeight = 0.11f;

    /// <summary>Speed of a ground pass at <paramref name="power01"/>; exposed because the planner shares it.</summary>
    public static float GroundPassSpeed(float power01) =>
        Math.Clamp(8.0f + Math.Clamp(power01, MinPower, MaxPower) * 11.5f, 8.0f, 19.5f);

    /// <summary>
    /// Launch of one pass-shaped kick. <paramref name="directionXZ"/> is the horizontal aim, normalized here;
    /// <paramref name="curveSpin"/> is the command's requested side spin (the AI passes zero).
    ///
    /// Only the five pass-shaped intents are covered — <see cref="KickIntentType.GroundPass"/> and
    /// <see cref="KickIntentType.ThroughPass"/> share a launch, as do
    /// <see cref="KickIntentType.LoftedPass"/> and <see cref="KickIntentType.LobbedThroughPass"/>. A shot's
    /// launch is solved from the goal geometry and deliberately does not live here; asking for anything else
    /// returns a zero launch, which the execution never does.
    /// </summary>
    public static KickLaunch For(KickIntentType intent, float power01, Vector2 directionXZ, float curveSpin = 0f)
    {
        float power = float.IsFinite(power01) ? Math.Clamp(power01, MinPower, MaxPower) : MinPower;
        float spin = float.IsFinite(curveSpin) ? curveSpin : 0f;

        Vector2 dir = directionXZ.LengthSquared() > 1e-6f && SimMath.IsFinite(directionXZ)
            ? Vector2.Normalize(directionXZ)
            : new Vector2(1f, 0f);

        switch (intent)
        {
            case KickIntentType.GroundPass:
            case KickIntentType.ThroughPass:
            {
                // Controlled, easy-to-receive pass speed (8.5 to 19.5 m/s), rolling on the deck.
                float speed = GroundPassSpeed(power);
                return new KickLaunch(
                    new Vector3(dir.X * speed, 0.05f, dir.Y * speed),
                    new Vector3(0f, spin * 8.0f, 0f));
            }

            case KickIntentType.DrivenPass:
            {
                // A fast, flat ball: it skips off the surface rather than rolling to its target.
                float speed = Math.Clamp(14.0f + power * 9.0f, 13.5f, 23.0f);
                return new KickLaunch(
                    new Vector3(dir.X * speed, 0.9f + power * 0.9f, dir.Y * speed),
                    new Vector3(-dir.Y * 18.0f, spin * 14.0f, dir.X * 18.0f));
            }

            case KickIntentType.LoftedPass:
            case KickIntentType.LobbedThroughPass:
            {
                float horizSpeed = Math.Clamp(9.5f + power * 11.5f, 9.0f, 21.0f);
                float vertSpeed = Math.Clamp(5.2f + power * 4.5f, 5.0f, 9.8f);
                return new KickLaunch(
                    new Vector3(dir.X * horizSpeed, vertSpeed, dir.Y * horizSpeed),
                    new Vector3(-dir.Y * 16.0f, spin * 22.0f, dir.X * 16.0f));
            }

            case KickIntentType.Cross:
            {
                // Whipped: a higher horizontal band than the lofted pass, with the spin axis reversed so the
                // ball bends towards the goal mouth rather than away from it.
                float horizSpeed = Math.Clamp(12.5f + power * 11.5f, 12.0f, 24.0f);
                float vertSpeed = Math.Clamp(5.5f + power * 4.0f, 5.2f, 9.5f);
                return new KickLaunch(
                    new Vector3(dir.X * horizSpeed, vertSpeed, dir.Y * horizSpeed),
                    new Vector3(dir.Y * 22.0f, spin * 40.0f, -dir.X * 22.0f));
            }

            default:
                return default;
        }
    }
}
