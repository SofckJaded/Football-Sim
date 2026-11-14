using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Physics;

namespace FootballSim.Training;

/// <summary>
/// Stationary AI passing partner for the Passing &amp; Receiving drills. It keeps the ball at its
/// feet until it is allowed to pass — either immediately (<c>passOnlyOnRequest == false</c>) or only
/// after the trainee presses the existing Pass control to call for it.
/// </summary>
public sealed class TrainingPasserController : IFootballerController
{
    private const float MaxReceivableSpeed = 8.0f;
    private const float PassCooldownSeconds = 0.9f;

    private readonly int _targetPlayerId;
    private readonly MatchCommunicationContext _communication;
    private readonly bool _passOnlyOnRequest;
    private readonly PassPlanner _passPlanner;

    private float _nextPassAllowedTime;
    private string _debugSummary = "Passer:Waiting";

    public TrainingPasserController(
        int targetPlayerId,
        MatchCommunicationContext communication,
        bool passOnlyOnRequest,
        PitchDimensions? pitch = null)
    {
        _targetPlayerId = targetPlayerId;
        _communication = communication;
        _passOnlyOnRequest = passOnlyOnRequest;
        _passPlanner = new PassPlanner(pitch ?? new PitchDimensions());
    }

    public ControllerSourceType SourceType => ControllerSourceType.AI;
    public string GetDebugSummary() => _debugSummary;

    public FootballerCommand Decide(in FootballerObservation obs, float dt)
    {
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        float distanceToBall = Vector2.Distance(selfXZ, ballXZ);

        PerceivedEntity? target = null;
        foreach (PerceivedEntity mate in obs.VisiblePlayers)
        {
            if (mate.PlayerId != _targetPlayerId) continue;
            target = mate;
            break;
        }

        Vector2 lookXZ = target.HasValue
            ? new Vector2(target.Value.EstimatedPosition.X - obs.SelfPosition.X,
                          target.Value.EstimatedPosition.Z - obs.SelfPosition.Z)
            : new Vector2(obs.AttackingDirectionX, 0f);
        lookXZ = SimMath.SafeNormalize(lookXZ, new Vector2(obs.AttackingDirectionX, 0f));

        bool hasBall = distanceToBall <= obs.SelfAttributes.ReachRadius * 1.55f && obs.AvailableToKick;

        if (!hasBall)
        {
            _debugSummary = "Passer:WaitingForBall";
            return FacingOnly(lookXZ);
        }

        bool calledForPass = _communication.IsActiveCallFor(obs.SelfTeam) &&
                             _communication.HumanCallForPassPlayerId == _targetPlayerId;

        if (_passOnlyOnRequest && !calledForPass)
        {
            _debugSummary = "Passer:WaitingForCall";
            return FacingOnly(lookXZ);
        }

        if (obs.MatchTimeSeconds < _nextPassAllowedTime)
        {
            _debugSummary = "Passer:Cooldown";
            return FacingOnly(lookXZ);
        }

        if (!target.HasValue)
        {
            _debugSummary = "Passer:NoTarget";
            return FacingOnly(lookXZ);
        }

        Vector2 targetXZ = new(target.Value.EstimatedPosition.X, target.Value.EstimatedPosition.Z);
        Vector2 targetVelocityXZ = new(target.Value.EstimatedVelocity.X, target.Value.EstimatedVelocity.Z);

        // The trainee is human: cap the lead to their reaction range and never aim past the pitch.
        PassPlan plan = _passPlanner.PlanGroundPass(
            ballXZ,
            _targetPlayerId,
            targetXZ,
            targetVelocityXZ,
            MaxReceivableSpeed,
            maxLeadMeters: 5f);

        Vector2 aim = plan.Feasible
            ? plan.AimDirectionXZ
            : SimMath.SafeNormalize(targetXZ - ballXZ, lookXZ);
        float power = plan.Feasible ? plan.Power01 : 0.55f;

        _nextPassAllowedTime = obs.MatchTimeSeconds + PassCooldownSeconds;
        if (_passOnlyOnRequest)
            _communication.ClearCallForPass();

        _debugSummary = plan.Feasible
            ? $"Passer:PassTo#{_targetPlayerId}"
            : "Passer:DirectPass";

        return new FootballerCommand(
            DesiredMoveDirectionXZ: Vector2.Zero,
            DesiredFacingDirectionXZ: aim,
            Sprint: false,
            ShieldBall: false,
            RequestTackle: false,
            RequestJumpHeader: false,
            RequestKeeperDive: false,
            KeeperDiveDirection: Vector3.Zero,
            KickType: KickIntentType.GroundPass,
            KickAimDirectionXZ: new Vector3(aim.X, 0f, aim.Y),
            KickPower01: Math.Clamp(power, 0.20f, 0.98f),
            RequestedCurveSpin: 0f)
        {
            IntendedReceiverId = _targetPlayerId
        };
    }

    private static FootballerCommand FacingOnly(Vector2 lookXZ) => new(
        DesiredMoveDirectionXZ: Vector2.Zero,
        DesiredFacingDirectionXZ: lookXZ,
        Sprint: false,
        ShieldBall: false,
        RequestTackle: false,
        RequestJumpHeader: false,
        RequestKeeperDive: false,
        KeeperDiveDirection: Vector3.Zero,
        KickType: KickIntentType.None,
        KickAimDirectionXZ: new Vector3(lookXZ.X, 0f, lookXZ.Y),
        KickPower01: 0.2f,
        RequestedCurveSpin: 0f);
}
