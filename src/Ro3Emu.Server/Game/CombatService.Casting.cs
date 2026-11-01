using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>A single accepted cast, bound to its role, scene and generation (LC_Scene_Sync._skillEndNotify).</summary>
public sealed class ActiveSkillCast
{
    public required long Id { get; init; }
    public required long OwnerId { get; init; }
    public required long WorldScene { get; init; }
    public long SceneGeneration { get; init; }
    public Ro3Emu.Server.Game.Ownership.AttachmentLease? Attachment { get; init; }
    public required CastSkillReq Request { get; init; }
    public required JsonElement Row { get; init; }
    public required Vec3 Origin { get; set; }
    /// <summary>Accepted while the caster still walked (WalkSlack): its stop right after is where it cast from.</summary>
    public bool StartedWalking { get; set; }
    /// <summary>Where the server had the caster at the request: its steps until the stop are measured from here.</summary>
    public Vec3? WalkStart { get; init; }
    /// <summary>The walk speed (m/s) at the request, before a cast dismount slowed it: that stop may land as far from the
    /// origin as this speed covers since <see cref="WalkFromUtc"/>.</summary>
    public float ArrivalSpeed { get; init; }
    /// <summary>The caster's last walk message when the cast was accepted: the walk its stop can have covered starts there.</summary>
    public DateTime WalkFromUtc { get; init; }
    public required long StartMs { get; init; }
    public required int ChantMs { get; init; }
    public required int ActionMs { get; init; }
    public bool Cancelled { get; set; }
    public bool Resolved { get; set; }
    /// <summary>HP/SP and buff-pile costs still owed by a chanted cast; paid once when the chant completes.</summary>
    internal Func<Task<bool>>? ChantCost { get; set; }
    /// <summary>A server bullet is in flight (CombatService.ServerBullet): cancelling the cast no longer stops it landing.</summary>
    public bool Launched { get; set; }
    public SkillObject? Operator { get; init; }
    /// <summary>Extra effects remain bound to their equipped source after asynchronous publication.</summary>
    internal Func<bool>? SourceEligible { get; init; }
    internal Dictionary<long, long>? GuardianDamage { get; init; }
    internal SemaphoreSlim? GuardianGate { get; init; }
    internal HashSet<int>? GuardianOnce { get; init; }
    public long SourceId => Operator?.Uid ?? OwnerId;
    /// <summary>A server-started follow-up (CombatService.Procs): it does not roll further procs.</summary>
    internal bool Proc { get; init; }
    /// <summary>A follow-up hits the row's area even when its _iRangeType names one target (Auto Counter's front box).</summary>
    internal bool AreaProc { get; init; }
    /// <summary>A damage multiplier: a follow-up's (Auto Counter +50% after a parried hit), or Cross Ripper's blade count.</summary>
    internal double ProcScale { get; set; } = 1;
    /// <summary>A follow-up that deals a set amount instead of the formula (Death Bound's reflected damage).</summary>
    internal long ProcFixedDamage { get; init; }
    /// <summary>Monsters a status of this cast already reached (Tetra Vortex's "at least 1" guarantee).</summary>
    internal HashSet<long> StatusLanded { get; } = new();
    /// <summary>This cast's own cooldown ends once it lands (Cross Ripper thrown with every blade).</summary>
    internal bool ResetsCooldown { get; set; }
    /// <summary>The cooldowns this cast started on accept, handed back if it is cancelled before its hit.</summary>
    internal IReadOnlyList<CooldownStamp>? Cooldowns { get; set; }
}

/// <summary>One cooldown a cast set: its end, and what the group/mode held before (0 = none).</summary>
internal readonly record struct CooldownStamp(int Group, int Mode, bool Shared, long End, long PreviousMode, long PreviousShared);

public sealed partial class CombatService
{
    public const uint SkillMove = 31112, TrapDamage = 31128;

    /// <summary>Runs under the role lock once a cast is accepted, before the client hears of it, with the cast skill id (a flying mount takes off or lands, WorldCoreService.MountFlight).</summary>
    public List<Func<GameSession, int, Task>> BeforeCastSkill { get; } = [];

    /// <summary>WorldCoreService.CanLand: whether a flying mount's landing skill may be cast where the rider is.</summary>
    public Func<GameSession, bool>? CanLand { get; set; }

    /// <summary>
    /// 31102: row identity, ownership, selector, mode, all costs and cooldowns are checked before the cast commits.
    /// Target/position come from CastSkillReq; m_kSelectInfo uses relation masks, never _iDmgType as a selector.
    /// Evidence: LC_Skill_Function.SendSkill, SkillConfig.GetCost*, MG_Skill.proto.
    /// </summary>
    public Task CastAsync(GameSession s, CastSkillReq request) => CastAsync(s, request, lineup: false);

    /// <param name="lineup">One skill of the Skill Bar's Lineup, cast by the server for the player: a skill that cannot
    /// be cast now is skipped without a refusal, and the client gets no cast reply it did not ask for.</param>
    private async Task CastAsync(GameSession s, CastSkillReq request, bool lineup)
    {
        s.EndCutscene(); // a player who acts is out of the story; no attacking while immune
        LegacyOwnerScope? admission = null;
        OwnerNotices? notices = null;
        if (s.Owners is { } owners && s.Role is { } role)
            admission = await owners.EnterWithGateAsync("combat/cast", s.SkillCastGate, [ReservationKey.Role(role.Id)]);
        else await s.SkillCastGate.WaitAsync();
        try
        {
            using var state = await CharacterService.Lock(s);
            // Complete the runtime admission before any socket send can suspend. Frozen packets
            // retain the original order; an obsolete attachment cannot start a committed cast.
            if (s.Owners?.Current is { Notices: null } context) notices = new OwnerNotices(context);
            if (IsGuardianRequest(request)) await CastGuardianAsync(s, request.Clone());
            else await CastCoreAsync(s, request, lineup);
        }
        finally { notices?.Dispose(); s.SkillCastGate.Release(); admission?.Dispose(); }
        if (notices != null) await notices.FlushAsync();
    }

    /// <summary>A pet wrapper or its child effect: the guardian path validates and runs it (31102).</summary>
    private bool IsGuardianRequest(CastSkillReq request) => _data.Row("SkillConfig", request.SkillId) is { } row
        && GameData.Int(row, "_iSystemType") is GuardianPetRules.WrapperType or GuardianPetRules.EffectType;

    private async Task CastCoreAsync(GameSession s, CastSkillReq request, bool lineup = false)
    {
        var req = request.Clone();
        var row = EffectiveSkillRow(s, req.SkillId);
        var owned = req.OpUnitId == 0 || req.OpUnitId == s.Role?.Id ? null
            : s.SkillObjects.FirstOrDefault(u => u.Uid == req.OpUnitId && u.WorldScene == s.WorldScene && u.ExpiresMs > Now);
        int error = s.Role == null || s.Progress == null || s.Dead || s.SceneLoading ? ErrorCodes.SkillBadState
            : row == null ? ErrorCodes.SkillUnknown
            : req.OpUnitId != 0 && req.OpUnitId != s.Role.Id && owned == null ? ErrorCodes.SkillRequirements
            : !float.IsFinite(req.Face) || req.TargetPos is { } pos && !Finite(pos) ? ErrorCodes.Parameter : 0;
        int? own = null;
        if (error == 0)
        {
            req.SkillId = (int)GameData.Int(row!.Value, "_iID");
            own = owned == null ? OwnSkillAdmit?.Invoke(s, req.SkillId) : null;
            if (own is { } ownError && ownError != 0) error = ownError;
            else if (own == null && (owned != null ? !owned.Skills.Contains(req.SkillId)
                : !Learned(s, row!.Value) && s.GmCastSkill != request.SkillId && !GrantedSkill.Exists(granted => granted(s, req.SkillId))))
                error = ErrorCodes.SkillNotLearned;
            else if (!SkillModeAllowed(s, row!.Value)) error = ErrorCodes.SkillRequirements;
            else if (owned == null && !SkillConditionMet(s, row!.Value)) error = ErrorCodes.SkillRequirements; // weapon, mount, pendant
            else if (owned == null && !LandingAllowed(s, row!.Value)) error = ErrorCodes.SkillRequirements; // over water (inferred)
            else if (GameData.Int(row!.Value, "_iBasicType") != 1) error = ErrorCodes.SkillPassive;
            else if (_buffs.HasForbidden(s, new SkillConfig(row.Value).IsNormalAttack(_data)
                ? ForbiddenState.NormalAttack : ForbiddenState.CastSkill)) error = ErrorCodes.SkillBadState;
            // Hymn of the Lamb: "This skill cannot be cast while You are under any control effect" (category 25: Stun,
            // Knockback, Fear, Frozen, Petrify, Sleep). Fear leaves other skills castable; Blind or Immobilize do not stop it.
            else if (Script(row.Value) == "Praefatio" && _buffs.CategoriesOf(s).Contains(25))
                error = ErrorCodes.SkillBadState;
            else if (req.SkillId == PublicSkill("WorldTreeSkillId") && _buffs.HasForbidden(s, ForbiddenState.UseItem)) error = ErrorCodes.ItemUseForbidden;
            else if (req.SkillId == PublicSkill("WorldTreeSkillId") && !ItemAllowedInMap(s, (int)PublicSkill("WorldTreeItemId"))) error = ErrorCodes.ItemUseAreaForbidden;
            else if (req.IsExtraSkill && owned == null && GameData.Int(row.Value, "_iExtraType") == 0) error = ErrorCodes.SkillRequirements;
        }
        var origin = owned?.Pos ?? s.Position;
        if (error == 0)
        {
            int group = (int)GameData.Int(row!.Value, "_iSkillID"), mode = (int)GameData.Int(row.Value, "_iMode");
            if (own == null && Math.Max(s.SkillCds.GetValueOrDefault(group), s.SkillModeCds.GetValueOrDefault((group, mode))) > Now) error = ErrorCodes.SkillCooldown;
            // A live cast refuses a new one until it yields (_iInterruptedType). _bCancelCast is only the client's
            // cancel-button flag (LC_SkillInput_Handler_Default.CheckShowCancelBtn), not a licence to recast.
            else if (s.SkillCast is { Cancelled: false } active && Now < active.StartMs + active.ChantMs + active.ActionMs
                && (!new SkillConfig(row.Value).CancelCast || SwingInFlight(active, row.Value, req))) error = ErrorCodes.SkillBusy;
            else error = ValidateTarget(s, row.Value, req, origin, owned == null ? WalkSlack(s) : 0,
                _data.Row("SkillConfig", request.SkillId) is { } askedRow ? Range(askedRow) : 0);
        }
        // GmUnlimit's "no consumption" (inferred): no HP, SP, item, buff pile or energy cost.
        bool unlimited = (s.GmCombat & GmCombatSwitches.Unlimited) != 0;
        var costs = row is { } value && !unlimited ? ResolveCosts(s, value) : new CastCosts();
        long errorParam = 0;
        if (error == 0) (error, errorParam) = ValidateCosts(s, costs);
        if (error == 0 && !unlimited && !_buffs.CanSpendEnergy(s, row!.Value)) error = ErrorCodes.SkillEnergyShort;
        if (error != 0)
        {
            if (!lineup) await RefuseCastAsync(s, req, error, errorParam);
            return;
        }

        long id = ++_sessionIds;
        var lease = s.Attachment;
        long scene = s.SceneGeneration, worldScene = s.WorldScene;
        // E05c: the receipt is the paid action's admission. Repeating a protocol request
        // is a new intent and pays again; a lost writer ACK resolves this exact source.
        if (!await AdmitCastCostAsync(s, row!.Value, req, costs, id))
        {
            if (!lineup) await RefuseCastAsync(s, req, ErrorCodes.SkillCostShort, costs.Items[0].Entry);
            return;
        }
        if (lease?.Cancelled == true || !ReferenceEquals(s.Attachment, lease) ||
            s.SceneGeneration != scene || s.WorldScene != worldScene || s.SceneLoading) return;
        float arrival = s.Speed / 100f; // before BeforeCast: a cast dismount lowers Speed, not the ground the ride covered
        // Before the 31102 reply: LC_State_Unit_Skill holds a skill while m_bRideSystemMount is set, then drops it once
        // its time has passed, so a rider dismounted later by the 1 s mount tick swung unseen.
        foreach (var handler in BeforeCastSkill) await handler(s, req.SkillId);
        int turn = owned == null ? SwingTurnLeft(s.SkillCast, row!.Value) : 0;
        await CancelCastAsync(s, SkillInterruptedInfo.Types.SkillInterruptType.Skill);
        _eventAttacks.Remove(s);
        // Classic RO rule (rAthena skill_castend): a chanted skill spends HP/SP only when its chant completes,
        // so an interrupted chant costs nothing. Instant skills pay now. Items and energy stay paid on accept.
        bool chanted = GameData.Int(row!.Value, "_iChantFix") + GameData.Int(row.Value, "_iChantVar") > 0;
        if (!chanted) await PayCostsAsync(s, costs);
        if (!unlimited) await _buffs.SpendEnergyAsync(s, row!.Value);
        int lead = (owned == null && BeforeCast is { } before ? await before(s) : 0) + turn;
        if (!lineup) await s.SendGameAsync(UseSkill, new CastSkillRsp { SessionId = id, IsAutoCast = req.IsAutoCast });
        var target = FindMonster(s.WorldScene, req.TargetId);
        var playerTarget = EventTarget(s, req.TargetId);
        // _iIfAutoAtk 2 (enum_AutoNormalAttackType.AutoNormalAttack) follows the cast with the career's normal attack
        // (LC_AutoAttackAppendState: GetNormalSkill). Repeating the cast skill itself bypassed its cooldown: Blade
        // Smash and Sonic Slash fired every swing.
        int loopSkill = new SkillConfig(row!.Value).IsNormalAttack(_data) ? req.SkillId : _skills.NormalAttack(s);
        if (GameData.Int(row!.Value, "_iIfAutoAtk") == 2 && owned == null && target != null && CanHitMonster(s, target))
        {
            s.Attack = new AttackLoop
            {
                Target = target, SkillId = loopSkill,
                NextSwingMs = Now + lead + SkillMs(s, row.Value) + ChantMs(s, row.Value) + 1,
                StartX = s.Position.X, StartZ = s.Position.Z, StartedWalking = s.Walking, StartMs = Now, ArrivalSpeed = arrival,
                WalkFromUtc = s.LastWalkUtc,
            };
        }
        else s.Attack = null;
        if (GameData.Int(row.Value, "_iIfAutoAtk") == 2 && owned == null && playerTarget != null)
            _eventAttacks[s] = new EventAttack(playerTarget, loopSkill,
                Now + lead + SkillMs(s, row.Value) + ChantMs(s, row.Value) + 1,
                s.Position.X, s.Position.Z);
        IReadOnlyList<CooldownStamp>? cooldowns = null;
        if (own != null) await OwnSkillCast!(s, req.SkillId);
        else cooldowns = await StartCooldownAsync(s, row.Value, askedMode: SkillConfig.Get(_data, request.SkillId) is { } asked ? (int)asked.Mode : null);
        await StartCastAsync(s, row.Value, req, id, owned, chanted ? costs : null, arrival, cooldowns, lead);
        if (owned == null && !lineup && new SkillConfig(row.Value).SystemType == SkillService.QueueSystemType)
            StartLineup(s, s.SkillCast);
    }

    /// <summary>
    /// The Skill Bar (SkillConfig._iSystemType 6, _kScriptLogic jinengduilie*): "Activating the Skill Bar casts each skill
    /// in the Skill Bar that currently meets its activation requirements once" (client text). The client only lists,
    /// checks and shows the Lineup: its button sends a plain 31102 for the Skill Bar row (UI_Sys_Main_JoyStickSkillQueueItem
    /// WidgetCtrl, usable while any member is off cooldown) and on its success lights the members' dots in slot order,
    /// skipping those on cooldown (_startShowSkillQueueFX); it never sends the members. The casting is this script (inferred
    /// timing): each member is cast on the player in slot order, one after the previous one's action (a new cast ends the
    /// running one, and an ended cast never lands), and one that cannot be cast now (cooldown, SP, state) is skipped.
    /// </summary>
    private void StartLineup(GameSession s, ActiveSkillCast? bar)
    {
        if (bar == null || s.SkillPlans is not { } plans || s.Progress == null) return;
        var members = plans.SkillAssignPlans[plans.CurrentPlanIndex].SkillQueue.OrderBy(kv => kv.Key)
            .Select(kv => kv.Value).Where(m => m.SkillId > 0).Select(m => (m.SkillId, m.SkillLevel)).ToList();
        if (members.Count == 0) return;
        var run = new object();
        s.LineupRun = run; // a second press replaces a Lineup still running (inferred)
        var step = new LineupStep(s, run, members, s.SceneGeneration, s.WorldScene);
        ScheduleRuntime(step.WorldScene, TimeSpan.FromMilliseconds(Math.Max(0, bar.StartMs - Now) + bar.ChantMs + bar.ActionMs + 1),
            () => LineupStepAsync(step, 0, waited: false));
    }

    private sealed record LineupStep(GameSession Session, object Run, List<(int Group, int Level)> Members, long Generation, long WorldScene);

    private async Task LineupStepAsync(LineupStep step, int index, bool waited)
    {
        var s = step.Session;
        for (; index < step.Members.Count; index++, waited = false)
        {
            if (!ReferenceEquals(s.LineupRun, step.Run) || s.Dead || s.SceneLoading || s.Role == null || s.Progress == null
                || s.SceneGeneration != step.Generation || s.WorldScene != step.WorldScene)
                return;
            // Another cast still in its action (the player's own, pressed meanwhile) would refuse the member as busy:
            // the member waits for it once, then is skipped if a cast is still running (inferred).
            if (!waited && s.SkillCast is { Cancelled: false } running && Now < running.StartMs + running.ChantMs + running.ActionMs)
            {
                int resume = index;
                ScheduleRuntime(step.WorldScene, TimeSpan.FromMilliseconds(running.StartMs + running.ChantMs + running.ActionMs + 1 - Now),
                    () => LineupStepAsync(step, resume, waited: true));
                return;
            }
            var (group, level) = step.Members[index];
            int learned = _skills.Level(s.Progress, group);
            int id = learned > 0 ? _skills.RealId(group, Math.Min(level, learned)) : 0;
            if (id <= 0) continue;
            await CastAsync(s, new CastSkillReq { SkillId = id, TargetId = s.Role.Id, TargetPos = s.Position.Clone(), Face = s.Face }, lineup: true);
            if (s.SkillCast is not { Cancelled: false } cast || cast.Request.SkillId != id) continue; // skipped
            int next = index + 1;
            if (next < step.Members.Count)
                ScheduleRuntime(step.WorldScene, TimeSpan.FromMilliseconds(Math.Max(0, cast.StartMs - Now) + cast.ChantMs + cast.ActionMs + 1),
                    () => LineupStepAsync(step, next, waited: false));
            else if (ReferenceEquals(s.LineupRun, step.Run)) s.LineupRun = null;
            return;
        }
        if (ReferenceEquals(s.LineupRun, step.Run)) s.LineupRun = null;
    }

    /// <summary>Whether the client's stop right after a cast (in-game 11.10) lands within the distance its walk can have
    /// covered since the last walk message before the cast. The client sends a walk only when its direction changes
    /// (in-game 22.10: straight legs of 0.86 s and 0.9 s), so the time is not capped below the 5 s
    /// SceneService.AcceptWalkAsync allows.</summary>
    private static bool WithinArrival(float speed, DateTime walkFrom, float distance) =>
        distance <= speed * Math.Clamp((DateTime.UtcNow - walkFrom).TotalSeconds, 0, MaxWalkSlackSeconds) + 0.5;

    /// <summary>
    /// Whether a live cast gives way to a new skill, as LC_Skill_RunTimeContext.SkillCanInterrupted and
    /// GetSkillCanInterrupted decide it: _iInterruptedType 1 (normal attacks) yields to any other skill at once but never
    /// to itself; 2 yields once the template's m_fInterruptTime has passed (never without one); any other type holds
    /// until the cast ends. An interrupt point inside the chant counts from the chant's end, as the client sets it.
    /// </summary>
    internal bool CastYieldsTo(ActiveSkillCast active, JsonElement next)
    {
        // High-Energy Impact's flight (its 5 s action) is cut short by "Tap the skill again".
        if (FlightScript(Script(active.Row)) && FlightAttackScript(Script(next))) return true;
        switch (GameData.Int(active.Row, "_iInterruptedType"))
        {
            case 1:
                // (inferred) A swing whose hit already landed on a target that is now dead or gone has nothing left to
                // deal, so a press on the next target starts at once instead of waiting out the animation tail.
                return GameData.Int(next, "_iSkillID") != GameData.Int(active.Row, "_iSkillID")
                    || active.Resolved && !TargetAlive(active.WorldScene, active.Request.TargetId);
            case 2:
                return InterruptPointPassed(active);
            default:
                return false;
        }
    }

    /// <summary>Type 2's interrupt point (the template's m_fInterruptTime, never inside the chant) has passed.</summary>
    private bool InterruptPointPassed(ActiveSkillCast active)
    {
        var template = _data.SkillTemplate(GameData.Int(active.Row, "_iScriptShow"));
        if (template is not { } t || !t.TryGetProperty("m_fInterruptTime", out var value)
            || GameData.Number(value) is not double point || point <= 0) return false;
        return Now >= active.StartMs + Math.Max(active.ChantMs, ImpactOffset(template, point, active.ChantMs, active.ActionMs));
    }

    /// <summary>
    /// Whether move input breaks a live cast. While the server forbids walking, the client turns a move key or a
    /// ground click into a bare 31124 (LC_UnitChecker.CheckClearCacheSkill: Skill state, a cached skill and
    /// IsSkillBlockMove) and stays in the skill unless the server agrees (LC_Scene_Sync._skillInterrupt). Moving
    /// cancels a cast (Steven's call, 2026-10-19), except in its first MoveCancelGraceMs: the click that places a
    /// ground skill can come back as one of these requests right after the cast starts, and granting it dropped the
    /// spell (inferred window). Normal attacks (_iInterruptedType 1) and casts past their hit break at once.
    /// </summary>
    private bool MoveBreaksCast(ActiveSkillCast active) => active.Resolved || active.Launched
        || GameData.Int(active.Row, "_iInterruptedType") == 1 || Now >= active.StartMs + MoveCancelGraceMs;

    /// <summary>How long after a cast starts move input cannot cancel it (the placing click, see MoveBreaksCast).</summary>
    internal const int MoveCancelGraceMs = 300;

    private bool TargetAlive(long sceneId, long uid) => uid != 0 && (FindMonster(sceneId, uid) != null
        || _world.PlayersIn(sceneId).Any(p => p.Role?.Id == uid && !p.Dead));

    /// <summary>Cast item cost and frozen intent share one owner receipt; HP/SP/buffs remain runtime resources.
    /// The admission uses the same scheduled cast and lethal pipeline as every ordinary skill.</summary>
    private async Task<bool> AdmitCastCostAsync(GameSession s, JsonElement row, CastSkillReq request, CastCosts costs, long id)
    {
        if (costs.Items.Count == 0) return true;
        var mutation = _character.ProgressionEconomy(s, "cd13c/cast-admit", costs.Items, [],
            new
            {
                SessionId = id, Skill = row.GetRawText(), Request = Convert.ToBase64String(Google.Protobuf.MessageExtensions.ToByteArray(request)),
                Plan = s.SkillPlans?.CurrentPlanIndex, Attachment = s.Attachment?.Generation,
                s.WorldScene, s.SceneGeneration, AttributeCosts = costs.Attrs, BuffCosts = costs.Buffs,
                EnergyCosts = costs.Energy, Outcome = "admitted"
            });
        return await _character.ExchangeLockedAsync(s, costs.Items, [], popup: false,
            persist: tx => tx.WithEconomy(mutation), asyncCommit: true) != null;
    }

    private Task RefuseCastAsync(GameSession s, CastSkillReq req, int error, long parameter) => RefuseCoreAsync(s, req, error, parameter);

    // Refusal must not interrupt a different accepted cast: the client does not compare 31103.sessionId.
    private async Task RefuseCoreAsync(GameSession s, CastSkillReq req, int error, long parameter)
    {
        var reply = new CastSkillRsp { Result = error, IsAutoCast = req.IsAutoCast };
        if (parameter != 0) reply.Params.Add(parameter);
        await s.SendGameAsync(UseSkill, reply);
        if (s.SkillCast is not { Cancelled: false })
        {
            var end = new SkillEndNotify { SrcID = s.Role?.Id ?? 0, Type = SkillEndNotify.Types.SkillEndType.Interrupted, Result = error };
            if (parameter != 0) end.Params.Add(parameter);
            await s.SendGameAsync(SkillEnd, end);
        }
    }

    /// <summary>
    /// A normal attack (_bCancelCast 1) may cut a skill's action short, but not the swing before it at the same target:
    /// otherwise a burst of clicks restarts the swing each time and hits at click speed (issue #121). The client paces swings
    /// at the same ASPD-scaled action time (the normal attack shows' m_fInterruptTime is their whole length) from its own
    /// send, so network jitter can bring a legitimate next swing a little early: <see cref="SwingJitterMs"/> of slack (inferred).
    /// </summary>
    private bool SwingInFlight(ActiveSkillCast active, JsonElement row, CastSkillReq req) =>
        req.TargetId != 0 && req.TargetId == active.Request.TargetId && SwingTurnLeft(active, row) > 0;

    /// <summary>
    /// Turning to another target during a swing is the client's own business (it plays the swing out), so the turn is taken,
    /// but the new swing starts when the one in flight would have ended: the milliseconds left, 0 when it is no such turn.
    /// Starting it at once let clicks alternating between two targets swing at click speed (inferred from the same pacing).
    /// </summary>
    private int SwingTurnLeft(ActiveSkillCast? active, JsonElement row)
    {
        if (active is not { Cancelled: false } || !new SkillConfig(row).IsNormalAttack(_data) || !new SkillConfig(active.Row).IsNormalAttack(_data))
            return 0;
        long left = active.StartMs + active.ChantMs + active.ActionMs - SwingJitterMs - Now;
        return left > 0 ? (int)Math.Min(left, int.MaxValue) : 0;
    }

    internal const int SwingJitterMs = 150;

    private bool Learned(GameSession s, JsonElement row)
    {
        if (GameData.Int(row, "_iID") == PublicSkill("WorldTreeSkillId")) return true; // paid item skill, LC_ReviveHelper.
        // A Survivor Battle career owns the match skill set (TimedBattleService.Chicken), not the job tree.
        if (MatchSkillRule?.Invoke(s, (int)GameData.Int(row, "_iID")) is { } granted) return granted;
        int normal = _skills.NormalAttack(s);
        if (GameData.Int(row, "_iID") == normal) return true;
        // Only the normal attack of the weapon and battle mount in use (SkillService.NormalAttack): the career's others
        // count as learned for the skill window, but an Archer without a bow does not shoot at bow range.
        int group = (int)GameData.Int(row, "_iSkillID");
        if (_skills.NormalGroups(s.Progress!.Career).Contains(group)
            && _data.Row("SkillConfig", normal) is { } current && GameData.Int(current, "_iSkillID") != group) return false;
        // The flying mount's take-off on the ground, its landing in the air (MountService.FlySkills, SkillWheelMount1).
        if (MountService.FlySkills(s, _data) is { } fly && GameData.Int(row, "_iID") == (s.WorldState.Flying ? fly.Down : fly.Up)) return true;
        if (ChainOffers(s, row)) return true; // Thousand Shadows, Shadow Loop, Shadow Echo's return (CombatService.Chains)
        return _skills.Level(s.Progress, (int)GameData.Int(row, "_iSkillID")) >= GameData.Int(row, "_iLevel");
    }

    /// <summary>A flying mount's landing skill needs walkable ground under the rider (<see cref="CanLand"/>).</summary>
    private bool LandingAllowed(GameSession s, JsonElement row) =>
        !s.WorldState.Flying || MountService.FlySkills(s, _data) is not { } fly || GameData.Int(row, "_iID") != fly.Down
        || CanLand?.Invoke(s) != false;

    private MonsterUnit? FindMonster(long sceneId, long uid) => _world.Map(sceneId).Units.OfType<MonsterUnit>().FirstOrDefault(m => m.Uid == uid && !m.Dead);
    private GameSession? FindPlayer(GameSession s, long uid, bool dead = false) => _world.PlayersIn(s.WorldScene).FirstOrDefault(p => p.Role?.Id == uid && p.Dead == dead);
    private long PublicSkill(string key) => _data.Table("PublicParamConfig").TryGetValue(key, out var value) ? (long)(GameData.Number(value) ?? 0) : 0;
    private static bool Finite(Vec3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static float Distance(Vec3 a, Vec3 b) => MonsterService.Distance(a.X, a.Z, b.X, b.Z);
    private static float Range(JsonElement row) => Math.Max(0, GameData.Int(row, "_iDistanceMax") / 100f);
    private static string Script(JsonElement row) => row.TryGetProperty("_kScriptLogic", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double[] Numbers(JsonElement row, string key) => row.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
        ? [.. list.EnumerateArray().Select(v => GameData.Number(v) ?? 0)] : [];
    private static IEnumerable<JsonElement> Tuples(JsonElement row, string key) => row.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Array) : [];

    private bool Relation(JsonElement row, bool friendly, bool self = false, int race = 0, bool dead = false)
    {
        var template = _data.SkillTemplate(GameData.Int(row, "_iScriptShow"));
        if (template is not { } t || !t.TryGetProperty("m_kSelectInfo", out var selectors) || selectors.ValueKind != JsonValueKind.Array)
            return friendly == (GameData.Int(row, "_iDmgType") == 3); // (inferred) typed recovery fallback for rows without a visual selector.
        foreach (var select in selectors.EnumerateArray())
        {
            int relation = (int)GameData.Int(select, "m_kSelectType"), subtype = (int)GameData.Int(select, "m_kSelectSubType");
            if ((relation & (dead ? friendly ? 32 : 16 : friendly ? 2 : 1)) == 0 && (relation & (dead ? 64 : 4)) == 0) continue;
            if (subtype == 2 && !self || subtype == 16 && self) continue;
            long raceMask = GameData.Int(select, "m_kSelectRaceType");
            if (!friendly && raceMask > 0 && (race <= 0 || (raceMask & (1L << (race - 1))) == 0)) continue;
            return true;
        }
        return false;
    }

    // The client casts on reaching range while it still walks; its stop arrives just after the cast (in-game 11.10:
    // 13 ms later, refused 3024 "invalid skill distance" on most approaches). The cast carries no caster position, so
    // a walking caster's range allows the distance it can have covered since its last walk. The client sends a walk
    // only when its direction changes (in-game 22.10: straight legs of 0.86 s and 0.9 s), so the time is not capped
    // below the 5 s SceneService.AcceptWalkAsync allows.
    internal const double MaxWalkSlackSeconds = 5;
    private static float WalkSlack(GameSession s) => s.WalkReportLag(MaxWalkSlackSeconds);

    /// <summary>
    /// Where along the walk in progress the caster can be nearest to the target: the walk goes straight from the last walk's
    /// position in its direction (WalkReq.dir, or the facing), <paramref name="travelled"/> metres at most. The slack is no
    /// circle: a forged "walking" report does not reach behind or beside the caster. <see cref="LegJitter"/> metres of
    /// sideways tolerance (inferred) cover a direction the client rounds.
    /// </summary>
    private static Vec3 WalkPoint(GameSession s, Vec3 origin, Vec3 target, float travelled)
    {
        var best = origin;
        float bestDistance = Distance(origin, target);
        foreach (float degrees in new[] { s.WalkDir, s.Face })
        {
            double angle = degrees * Math.PI / 180;
            float ux = (float)Math.Sin(angle), uz = (float)Math.Cos(angle);
            float along = Math.Clamp((target.X - origin.X) * ux + (target.Z - origin.Z) * uz, 0, travelled);
            var point = new Vec3 { X = origin.X + ux * along, Y = origin.Y, Z = origin.Z + uz * along };
            if (Distance(point, target) < bestDistance) (best, bestDistance) = (point, Distance(point, target));
        }
        return best;
    }

    internal const float LegJitter = 1f;

    /// <param name="askedRange">The range of the row the client asked for: it walks into that range, not into the
    /// range of a mode row a talent or a mount selects (Arrow Smash 18 m, its wolf row 5 m; issue #62).</param>
    private int ValidateTarget(GameSession s, JsonElement row, CastSkillReq req, Vec3 origin, float slack = 0, float askedRange = 0)
    {
        int release = (int)GameData.Int(row, "_iRelease");
        var monster = FindMonster(s.WorldScene, req.TargetId);
        bool revive = Script(row) == "Resurrection";
        var player = req.TargetId == s.Role!.Id ? s : FindPlayer(s, req.TargetId, revive);
        var enemyPlayer = EventTarget(s, req.TargetId);
        Vec3 targetPosition;
        if (release == 1)
        {
            if (monster != null && CanHitMonster(s, monster) && Relation(row, false, race: monster.Template.Race)) targetPosition = monster.Pos;
            else if (!revive && enemyPlayer != null) targetPosition = enemyPlayer.Position;
            else if (player != null && player.Dead == revive && Relation(row, true, player == s, dead: revive)
                && (!revive || !_buffs.HasForbidden(player, ForbiddenState.Revive))) targetPosition = player.Position;
            else if (req.TargetId == 0 && Relation(row, true, true)) { req.TargetId = s.Role.Id; targetPosition = s.Position; }
            else return ErrorCodes.SkillTargetInvalid;
            req.TargetPos = targetPosition.Clone();
            if (revive)
            {
                if (player == s) return ErrorCodes.SkillTargetInvalid;
                if (req.SkillId == PublicSkill("WorldTreeSkillId") ? ReviveItemSkill == null : ReviveOffer == null)
                    return ErrorCodes.SkillReviveNotAllowed;
                if (ReviveCheck?.Invoke(player!) is { } reviveError && reviveError != 0) return reviveError;
            }
        }
        else if (release is 7 or 8)
        {
            if (req.TargetPos == null) return ErrorCodes.SkillTargetInvalid;
            targetPosition = req.TargetPos;
        }
        else
        {
            // No release target: the hit picks req.TargetId (single-target rows) and summons or pulls use req.TargetPos,
            // so both must lie within the row's reach of the caster. One beyond it is dropped rather than refused, so a
            // client that sends a stale target still casts (Auto Battle does not retry a refusal).
            float aim = Math.Max(5f, Math.Max(Math.Max(Range(row), askedRange), new SkillConfig(row).Range.FirstOrDefault(0) / 100f)) + 1.5f + slack;
            if (req.TargetPos == null || Distance(origin, req.TargetPos) > aim) req.TargetPos = origin.Clone();
            if (req.TargetId != 0 && req.TargetId != s.Role.Id
                && (monster?.Pos ?? enemyPlayer?.Position ?? player?.Position) is { } at && Distance(origin, at) > aim)
                req.TargetId = 0;
            return 0;
        }
        if (!_buffs.CanSeePosition(s, targetPosition)) return ErrorCodes.SkillTargetInvalid;
        float d = Distance(origin, targetPosition);
        float reach = slack > 0 ? Distance(WalkPoint(s, origin, targetPosition, slack), targetPosition) - LegJitter : d;
        if (d < new SkillConfig(row).DistanceMin / 100f - slack || reach > Math.Max(Range(row), askedRange) + 0.5f)
            return ErrorCodes.SkillDistance;
        float height = GameData.Int(row, "_iHeightRange") / 100f;
        if (height > 0 && MathF.Abs(origin.Y - targetPosition.Y) > height) return ErrorCodes.SkillHeight;
        // The client's skill ray (RoSpace -> SceneSpaceDynamic.CheckInDirectPassFromTo with _iPassBlock as pass level) is
        // stopped only by an active air wall whose gridLevel exceeds that level: static cells never stop it, because
        // RoSpace builds the scene space without grid levels (RoSpace.LoadSpace -> SceneSpaceStatic.AddHeight, so
        // SceneSpaceStatic.IsBlocked compares level 0). Air walls are switched on by dungeon variable 133 CAirWallList,
        // which this server sets nowhere, so on an open map a tree or a wall corner between caster and target refuses
        // nothing (auto-combat kept re-casting at a target the client saw as free). A siege keeps its (inferred) walls
        // through SkillLineBlocked.
        if (new SkillConfig(row).PassBlock == 2
            && SkillLineBlocked?.Invoke(s, release == 1 ? monster : null, origin, targetPosition) == true)
            return ErrorCodes.SkillBlocked;
        return 0;
    }

    /// <summary>The scene's item limit (<see cref="ItemMapLimit"/>), in a dungeon through its DungeonConfig row.</summary>
    private bool ItemAllowedInMap(GameSession s, int entry) =>
        _data.Row("ItemConfig", entry) is { } item && ItemMapLimit.Allows(_data, s.SceneEntry, item);

    private sealed class CastCosts
    {
        public Dictionary<int, long> Attrs { get; } = [];
        public List<(int Entry, long Count)> Items { get; } = [];
        public Dictionary<int, long> Buffs { get; } = [];
        public Dictionary<int, long> Energy { get; } = [];
        public bool Invalid { get; set; }
    }

    private CastCosts ResolveCosts(GameSession s, JsonElement row)
    {
        var result = new CastCosts();
        var stats = _character.Stats(s);
        foreach (var tuple in Tuples(row, "_kCost"))
        {
            var c = tuple.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
            // (inferred) retained opaque type-4 markers follow the live client checker, which skips them.
            // Lua_GameDefine.enum_SkillCostType / LC_Skill_Function.CheckSkillCost only consume 1/2/3/6/7/8.
            if (c.Length == 2 && c[0] == 4) continue;
            if (c.Length < 3) { result.Invalid = true; continue; }
            int key = (int)c[1];
            long amount = c.Length > 3 ? c[3] : c[2];
            switch (c[0])
            {
                case 1 when c.Length >= 4 && key is 2000 or 2001:
                    amount += stats[key == 2000 ? 101 : 102] * c[2] / 10000;
                    result.Attrs[key] = result.Attrs.GetValueOrDefault(key) + Math.Max(0, amount);
                    break;
                case 7 when c.Length >= 4 && key is 2000 or 2001:
                    // AttrConvert [7, resource, basis-property, ratio], LC_Skill_Function cost checker.
                    result.Attrs[key] = result.Attrs.GetValueOrDefault(key) + Math.Max(0, stats[(int)c[2]] * c[3] / 10000);
                    break;
                case 2: result.Items.Add((key, Math.Max(0, c[2]))); break;
                case 3: result.Buffs[key] = result.Buffs.GetValueOrDefault(key) + Math.Max(0, c[2]); break;
                case 8: result.Energy[key] = result.Energy.GetValueOrDefault(key) + Math.Max(0, amount); break;
                default: result.Invalid = true; break; // reject an unsupported cost rather than grant a free cast.
            }
        }
        if (result.Attrs.TryGetValue(AttributeId.Sp, out long sp)) result.Attrs[AttributeId.Sp] = ModifiedSpCost(stats, sp);
        return result;
    }

    private (int Error, long Parameter) ValidateCosts(GameSession s, CastCosts costs)
    {
        if (costs.Invalid) return (ErrorCodes.SkillRequirements, 0);
        var stats = _character.Stats(s);
        foreach (var (attr, cost) in costs.Attrs)
            if (cost > stats[attr] || attr == 2000 && cost >= stats[attr]) return (ErrorCodes.SkillCostShort, attr);
        foreach (var (group, count) in costs.Buffs)
            if (!_buffs.HasBuffPiles(s, group, (int)count))
                return (ErrorCodes.SkillBuffCostShort, group);
        return (0, 0);
    }

    private async Task PayCostsAsync(GameSession s, CastCosts costs)
    {
        if (costs.Attrs.Count > 0)
        {
            var stats = _character.Stats(s);
            await _character.SetHpSpAsync(s, stats[2000] - costs.Attrs.GetValueOrDefault(2000), stats[2001] - costs.Attrs.GetValueOrDefault(2001));
        }
        foreach (var (group, count) in costs.Buffs)
            await _buffs.ConsumeBuffPilesAsync(s, group, (int)count);
    }

    /// <summary>Ends a skill group's running cooldowns now, each under the mode it was shown (a talent's reset).</summary>
    internal static async Task ResetCooldownAsync(GameSession s, int group)
    {
        long now = Now;
        var notice = new SkillCdNotify { Uuid = s.Role!.Id };
        foreach (var (key, end) in s.SkillModeCds.Where(c => c.Key.Group == group && c.Value > now).ToArray())
        {
            s.SkillModeCds.Remove(key);
            notice.Infos.Add(new SkillCdInfo { SkillId = group, SkillMode = key.Mode, CdStartTimestampMs = now, CdEndTimestampMs = now });
        }
        if (s.SkillCds.Remove(group) && notice.Infos.Count == 0)
            notice.Infos.Add(new SkillCdInfo { SkillId = group, CdStartTimestampMs = now, CdEndTimestampMs = now });
        if (notice.Infos.Count > 0) await s.SendGameAsync(SkillCdUpdate, notice);
    }

    /// <summary>Spends a chanted cast's deferred costs once. If the caster no longer has them, the cast fails
    /// like a refusal (31104 Interrupted with the cost error) and nothing is spent.</summary>
    private async Task<bool> PayChantCostAsync(GameSession s, ActiveSkillCast action, CastCosts costs)
    {
        action.ChantCost = null;
        var (error, parameter) = ValidateCosts(s, costs);
        if (error == 0)
        {
            await PayCostsAsync(s, costs);
            return true;
        }
        await CancelCastAsync(s, SkillInterruptedInfo.Types.SkillInterruptType.Skill, error, parameter);
        return false;
    }

    private async Task<IReadOnlyList<CooldownStamp>?> StartCooldownAsync(GameSession s, JsonElement row, bool landing = false, int? askedMode = null)
    {
        if ((s.GmCombat & GmCombatSwitches.NoCooldown) != 0) return null; // GmNoCD / GmUnlimit 1: no skill cooldown starts
        // High-Energy Impact's take-off shares its cooldown with the attack it arms; the cooldown starts on landing.
        if (!landing && FlightScript(Script(row))) return null;
        int group = (int)GameData.Int(row, "_iSkillID"), mode = (int)GameData.Int(row, "_iMode");
        long now = Now, cd = GameData.Int(row, "_iCD");
        var notice = new SkillCdNotify { Uuid = s.Role!.Id };
        var stamps = new List<CooldownStamp>();
        void Set(int g, int m, long duration, bool shared, int shownMode)
        {
            if (duration <= 0) return;
            long end = now + duration;
            long previousMode = s.SkillModeCds.GetValueOrDefault((g, m)), previousShared = s.SkillCds.GetValueOrDefault(g);
            s.SkillModeCds[(g, m)] = Math.Max(end, previousMode);
            if (shared) s.SkillCds[g] = Math.Max(end, previousShared);
            stamps.Add(new CooldownStamp(g, m, shared, end, previousMode, previousShared));
            notice.Infos.Add(new SkillCdInfo { SkillId = g, SkillMode = shownMode, CdStartTimestampMs = now, CdEndTimestampMs = end });
        }
        // The client files a 31113 under group * 10000 + its mode (LC_Skill_Function.GetSkillCDInfoKey) and its bar reads
        // the key of the row it keeps, the row it asks to cast. A mode a talent or a mount selects (type 15: Joint Beat,
        // Spear Throw, Arrow Smash on the wolf) casts another row, and a cooldown sent under that row's mode never showed
        // (issue #62): it goes under the asked row's mode. Without one, a mode row's _iCdModeGroup (0: mode 0's) decides.
        int cdModeGroup = (int)new SkillConfig(row).CdModeGroup;
        Set(group, mode, cd, cdModeGroup < 0, askedMode ?? (cdModeGroup >= 0 ? cdModeGroup : mode));
        var shared = Numbers(row, "_iPubCD");
        var groups = Numbers(row, "_iPubCDGroup");
        // Parallel arrays, e.g. BackSlide [15104,15330,15329] / [1000,15000,0] (inferred server application).
        for (int i = 0; i < Math.Min(shared.Length, groups.Length); i++)
        {
            int sibling = (int)groups[i];
            if (_data.Row("SkillConfig", sibling) is { } siblingRow) sibling = (int)GameData.Int(siblingRow, "_iSkillID");
            if (sibling > 0) Set(sibling, 0, (long)shared[i], true, 0);
        }
        if (notice.Infos.Count > 0) await s.SendGameAsync(SkillCdUpdate, notice);
        return stamps;
    }

    /// <summary>
    /// A cast cancelled before its hit did nothing, so it gives back the cooldowns it started on accept (classic RO
    /// starts them only when the cast lands, rAthena skill_castend). A group whose cooldown something else raised since
    /// keeps that later one. Items and energy stay spent, as on accept.
    /// </summary>
    private async Task RefundCooldownsAsync(GameSession s, ActiveSkillCast action)
    {
        if (action.Cooldowns is not { Count: > 0 } stamps || s.Role == null) return;
        action.Cooldowns = null;
        long now = Now;
        var notice = new SkillCdNotify { Uuid = s.Role.Id, NotPlayCdRefreshEffect = true };
        foreach (var stamp in stamps.Reverse()) // undo in reverse: a later stamp may have raised an earlier one's group
        {
            bool changed = false;
            if (s.SkillModeCds.GetValueOrDefault((stamp.Group, stamp.Mode)) == stamp.End)
            {
                if (stamp.PreviousMode > now) s.SkillModeCds[(stamp.Group, stamp.Mode)] = stamp.PreviousMode;
                else s.SkillModeCds.Remove((stamp.Group, stamp.Mode));
                changed = true;
            }
            if (stamp.Shared && s.SkillCds.GetValueOrDefault(stamp.Group) == stamp.End)
            {
                if (stamp.PreviousShared > now) s.SkillCds[stamp.Group] = stamp.PreviousShared;
                else s.SkillCds.Remove(stamp.Group);
                changed = true;
            }
            if (!changed) continue;
            long end = Math.Max(now, Math.Max(s.SkillModeCds.GetValueOrDefault((stamp.Group, stamp.Mode)),
                stamp.Shared ? s.SkillCds.GetValueOrDefault(stamp.Group) : 0));
            notice.Infos.Add(new SkillCdInfo { SkillId = stamp.Group, SkillMode = stamp.Mode, CdStartTimestampMs = now, CdEndTimestampMs = end });
        }
        if (notice.Infos.Count > 0) await s.SendGameAsync(SkillCdUpdate, notice);
    }

    /// <summary>Legacy auto-attack entry point, using the same cancellation-safe cast owner as manual skills.</summary>
    private async Task SwingAsync(GameSession s, MonsterUnit? target, int skillId, long id)
    {
        if (s.Role == null || s.Progress == null) return;
        using var held = await CharacterService.Lock(s);
        if (target != null && !CanHitMonster(s, target)) return;
        if (EffectiveSkillRow(s, skillId) is not { } row || !Learned(s, row) || !SkillModeAllowed(s, row)
            || _buffs.HasForbidden(s, ForbiddenState.NormalAttack) || s.SkillCast is { Cancelled: false }) return;
        var request = new CastSkillReq
        {
            SkillId = (int)GameData.Int(row, "_iID"), TargetId = target?.Uid ?? 0,
            TargetPos = target?.Pos ?? s.Position.Clone(), Face = s.Face
        };
        if (!await AdmitAutomaticCostAsync(s, row, request, id)) { s.Attack = null; return; }
        await StartCastAsync(s, row, request, id);
    }

    private async Task<bool> AdmitAutomaticCostAsync(GameSession s, JsonElement row, CastSkillReq request, long id)
    {
        var costs = ResolveCosts(s, row);
        if (costs.Items.Count == 0)
        {
            // No item receipt to admit, but HP/SP still apply: Sacrifice's basic-attack modes cost 30 HP a swing.
            if (costs.Attrs.Count == 0 && costs.Buffs.Count == 0) return true;
            if (ValidateCosts(s, costs).Error != 0) return false;
            await PayCostsAsync(s, costs);
            return true;
        }
        if (ValidateCosts(s, costs).Error != 0 || !_buffs.CanSpendEnergy(s, row)) return false;
        var lease = s.Attachment;
        long scene = s.SceneGeneration, worldScene = s.WorldScene;
        bool Current() => lease?.Cancelled != true && ReferenceEquals(s.Attachment, lease) &&
            s.SceneGeneration == scene && s.WorldScene == worldScene && !s.SceneLoading && !s.Dead;
        if (!await AdmitCastCostAsync(s, row, request, costs, id) || !Current()) return false;
        await PayCostsAsync(s, costs);
        await _buffs.SpendEnergyAsync(s, row);
        return Current();
    }

    /// <param name="leadMs">How long the cast waits before it starts (<see cref="BeforeCast"/>): its timeline is sent and its
    /// impact lands that much later, and an interrupt in between cancels it like any cast.</param>
    private async Task StartCastAsync(GameSession s, JsonElement row, CastSkillReq req, long id, SkillObject? actor = null, CastCosts? chantCosts = null, float? arrivalSpeed = null, IReadOnlyList<CooldownStamp>? cooldowns = null, int leadMs = 0)
    {
        var origin = actor?.Pos ?? s.Position.Clone();
        var target = FindMonster(s.WorldScene, req.TargetId);
        float face = target != null ? MonsterService.Angle(origin.X, origin.Z, target.X, target.Z) : req.Face;
        var action = new ActiveSkillCast
        {
            Id = id, OwnerId = s.Role!.Id, WorldScene = s.WorldScene, SceneGeneration = s.SceneGeneration,
            Attachment = s.Attachment, Request = req, Row = row, Origin = origin, StartedWalking = actor == null && s.Walking, WalkStart = origin.Clone(), ArrivalSpeed = arrivalSpeed ?? s.Speed / 100f,
            WalkFromUtc = s.LastWalkUtc,
            StartMs = Now + leadMs, ChantMs = ChantMs(s, row), ActionMs = SkillMs(s, row), Operator = actor,
            Cooldowns = cooldowns,
        };
        if (chantCosts is { } owed) action.ChantCost = () => PayChantCostAsync(s, action, owed);
        s.SkillCast = action;
        // A caster that may not walk while casting (_bMovement 0) saves up no walking distance until the cast ends: a client
        // that walks on without reporting it cannot claim the distance afterwards (SceneService.AcceptWalkAsync).
        if (actor == null && new SkillConfig(row).Movement == 0)
            SceneService.HoldWalk(s, DateTime.UtcNow.AddMilliseconds(leadMs + action.ChantMs + action.ActionMs), s.Walking);
        var template = _data.SkillTemplate(GameData.Int(row, "_iScriptShow"));
        var points = template is { } t ? Numbers(t, "m_kAtkPoints") : [];
        double point = points.FirstOrDefault(0.2);
        int at = Script(row) == "Resurrection" ? action.ChantMs : ImpactOffset(template, point, action.ChantMs, action.ActionMs);
        // A Lock skill (_bMovement 0) roots the caster until its hit. The client asks to cast while still walking
        // (LC_Skill_ActionPreCheck.CheckSkillSendServer sends its position, then USE_SKILL_REQ, without stopping) and
        // keeps walking unless the server forbids it: its joystick and path controllers stop only on StatusForbidden
        // Walk (LC_Unit_Player.IsSkillBlockMove). Without it the next step cancelled the cast as a move.
        // A hit inside the first loop tick (Grimtooth: m_kAtkPoints [0]) has nothing to hold still for, and the lock
        // flipping on and off around its dash (PerformEffect_Agent.SetBlockInputState shares the same StopRayCastMove /
        // LJ_BlockMove flags) left the client unable to walk until its next such skill (in game 10.18, inferred cause).
        bool locked = actor == null && GameData.Int(row, "_bMovement") == 0 && at > GameLoop.TickInterval.TotalMilliseconds;
        if (locked) await LockCastMovementAsync(s, id);
        try { await AnnounceAndScheduleCastAsync(s, row, req, action, actor, face, origin, at, leadMs); }
        catch
        {
            // Nothing is scheduled to lift the lock if the announcement failed.
            if (locked) await ReleaseCastMovementAsync(s, id);
            throw;
        }
    }

    private async Task AnnounceAndScheduleCastAsync(GameSession s, JsonElement row, CastSkillReq req, ActiveSkillCast action,
        SkillObject? actor, float face, Vec3 origin, int at, int leadMs)
    {
        long id = action.Id;
        s.WorldState.ResurrectionCastId = Script(row) == "Resurrection" ? id : 0;
        s.Face = face;
        req.Face = face;
        Task Timeline() => SendCastTimelineAsync(s, new UseInSkillInfo
        {
            SrcID = action.SourceId, SkillID = req.SkillId, TargetId = req.TargetId, TargetPos = req.TargetPos ?? origin,
            Face = face, SessionId = id, ChantMs = action.ChantMs, SkillMs = action.ActionMs, StartTime = action.StartMs, PlayAct = true,
        }, highLevel: GameData.Int(row, "_iSkillEffectLevel") > 0, extra: actor != null || req.IsExtraSkill);
        if (leadMs <= 0) await Timeline();
        else ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(leadMs), () => Live(s, action) ? Timeline() : Task.CompletedTask);
        if (action.ChantCost != null)
            ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(leadMs + Math.Min(at, action.ChantMs)), async () =>
            {
                if (Live(s, action) && action.ChantCost is { } pay) await pay();
            });
        ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(leadMs + at), async () =>
        {
            if (!Live(s, action)) return;
            // An impact on the chant boundary can run before the chant-end callback: settle the cost first.
            if (action.ChantCost is { } pay && !await pay()) return;
            action.Resolved = true;
            // Past its hit the client lets a move break the skill (LC_State_Unit_Skill ReadyToEnd + IsCanMoveBreak).
            await ReleaseCastMovementAsync(s, action.Id);
            await ResolveCastAsync(s, action);
        });
        ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(leadMs + Math.Max(at, action.ChantMs + action.ActionMs) + 1), async () =>
        {
            if (ReferenceEquals(s.SkillCast, action)) s.SkillCast = null;
            await ReleaseCastMovementAsync(s, action.Id); // a cast that never reached its hit (death, refused chant cost)
        });
    }

    private async Task LockCastMovementAsync(GameSession s, long castId)
    {
        s.CastMoveLockId = castId;
        await _buffs.RefreshCastLockAsync(s);
    }

    /// <summary>Lifts the cast's movement lock; a newer cast's lock stays.</summary>
    private async Task ReleaseCastMovementAsync(GameSession s, long castId)
    {
        if (s.CastMoveLockId != castId || castId == 0) return;
        s.CastMoveLockId = 0;
        await _buffs.RefreshCastLockAsync(s);
    }

    /// <summary>Piecewise template time mapping; same scale as LC_Skill_RunTimeContext (inferred for attack points).</summary>
    internal static int ImpactOffset(JsonElement? template, double point, int chantMs, int actionMs)
    {
        double chant = 0, end = 1;
        if (template is { } t && t.TryGetProperty("m_kState", out var state))
        {
            var times = Numbers(state, "Time");
            if (times.Length >= 3) { chant = times[1]; end = times[2]; }
        }
        double ms = point < chant && chant > 0 ? point / chant * chantMs : chantMs + Math.Clamp((point - chant) / Math.Max(0.001, end - chant), 0, 1) * actionMs;
        return (int)Math.Max(0, Math.Round(ms));
    }

    private static bool Live(GameSession s, ActiveSkillCast action) => !action.Cancelled && ReferenceEquals(s.SkillCast, action)
        && !s.Dead && !s.SceneLoading && s.Role?.Id == action.OwnerId && s.WorldScene == action.WorldScene
        && (action.Operator == null || !action.Operator.Removing && action.Operator.ExpiresMs > Now);

    private int SkillMs(GameSession s, JsonElement row)
    {
        double aspd = Math.Clamp(_character.Stats(s)[115] / 100.0, 0.1, 5);
        double influence = GameData.Int(row, "_iActionTimeInfluence") / 100.0;
        return (int)Math.Round(Math.Max(1, GameData.Int(row, "_iActionTime")) / Math.Max(0.1, 1 + (aspd - 1) * influence));
    }

    public async Task InterruptAsync(GameSession s, SkillInterruptReq? req = null)
    {
        // 31124 with a normal attack as nxtSkill during a swing: at the same target it is the click burst the cast rule refuses
        // (SwingInFlight), at another the turn CastCoreAsync paces (SwingTurnLeft), which needs the swing still in flight.
        if (req?.NxtSkill is { } nextSwing && s.SkillCast is { Cancelled: false } swinging
            && EffectiveSkillRow(s, nextSwing.SkillId) is { } nextRow && SwingTurnLeft(swinging, nextRow) > 0)
        {
            bool same = SwingInFlight(swinging, nextRow, nextSwing);
            await s.SendGameAsync(SkillInterrupt, new SkillInterruptResp { IsInterruptSucc = !same });
            if (same) await RefuseCastAsync(s, nextSwing, ErrorCodes.SkillBusy, 0);
            else await CastAsync(s, nextSwing);
            return;
        }
        bool valid = req == null || req.InterruptSkillId == 0 || req.InterruptSkillId == s.SkillCast?.Request.SkillId
            || req.InterruptSkillId == s.Attack?.SkillId || _eventAttacks.TryGetValue(s, out var eventAttack) && req.InterruptSkillId == eventAttack.SkillId;
        // A bare request is move input (see MoveBreaksCast): inside the grace window the cast carries on.
        if (valid && req is { InterruptSkillId: 0, NxtSkill: null } && s.SkillCast is { Cancelled: false } live
            && Now < live.StartMs + live.ChantMs + live.ActionMs && !MoveBreaksCast(live)) valid = false;
        bool interrupted = valid && (s.SkillCast != null || s.Attack != null || _eventAttacks.ContainsKey(s));
        if (interrupted)
        {
            s.Attack = null;
            _eventAttacks.Remove(s);
            await CancelCastAsync(s, req?.NxtSkill != null ? SkillInterruptedInfo.Types.SkillInterruptType.Skill : SkillInterruptedInfo.Types.SkillInterruptType.Move);
        }
        await s.SendGameAsync(SkillInterrupt, new SkillInterruptResp { IsInterruptSucc = interrupted });
        if (interrupted && req?.NxtSkill is { } next) await CastAsync(s, next);
    }

    private async Task CancelCastAsync(GameSession s, SkillInterruptedInfo.Types.SkillInterruptType cause, int result = 0, long parameter = 0)
    {
        s.WorldState.ResurrectionCastId = 0;
        if (s.SkillCast is not { } action) return;
        action.Cancelled = true;
        s.SkillCast = null;
        await ReleaseCastMovementAsync(s, action.Id);
        if (!action.Resolved && !action.Launched) await RefundCooldownsAsync(s, action);
        s.WalkAllowance.Release(DateTime.UtcNow);
        await ToPlayerAndViewersAsync(s, SkillEnd, new SkillEndNotify
        {
            SrcID = action.SourceId, SessionId = action.Id, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
            ClearEffect = !action.Launched, InterruptedInfo = new SkillInterruptedInfo { Type = cause }, Result = result,
            Params = { parameter == 0 ? [] : new[] { parameter } },
        });
    }

    public void PlayerMoved(GameSession s)
    {
        SwiftWargOnMove(s);
        if (_eventAttacks.TryGetValue(s, out var eventAttack)
            && MonsterService.Distance(eventAttack.StartX, eventAttack.StartZ, s.Position.X, s.Position.Z) > 1.5f) _eventAttacks.Remove(s);
        if (s.Attack is { } a && MonsterService.Distance(a.StartX, a.StartZ, s.Position.X, s.Position.Z) > 0.1f)
        {
            // Clicking an enemy from afar: the client attacks on reaching range and stops a few ms later (in-game
            // 11.10), so that first stop is where the auto-attack starts, not a step away that ends it.
            if (a.StartedWalking && !s.Walking && Now - a.StartMs <= 300
                && WithinArrival(a.ArrivalSpeed, a.WalkFromUtc, MonsterService.Distance(a.StartX, a.StartZ, s.Position.X, s.Position.Z)))
            {
                a.StartX = s.Position.X; a.StartZ = s.Position.Z;
                a.StartedWalking = false;
            }
            else s.Attack = null;
        }
        if (s.SkillCast is not { } cast || new SkillConfig(cast.Row).Movement != 0) return;
        // A walk that sets off (not a stop) after the arrival grace ends a cast that holds the caster in place, even before it
        // has moved: the client interrupts such a cast to walk, and the walk budget is held until the cast ends.
        if (Distance(cast.Origin, s.Position) <= 0.1f && !(s.Walking && Now - cast.StartMs > 300)) return;
        // The client casts while it still walks and stops only once it hears of the cast (in-game 11.10: 1 ms later on
        // reaching range; a cast pressed mid-walk stops on the movement lock). Its steps until that stop are the cast's
        // real origin, within the distance the walk can have covered, not a move that interrupts it.
        if (cast.StartedWalking && Now - cast.StartMs <= 300
            && WithinArrival(cast.ArrivalSpeed, cast.WalkFromUtc, Distance(cast.WalkStart ?? cast.Origin, s.Position)))
        {
            cast.Origin = s.Position.Clone();
            if (!s.Walking) cast.StartedWalking = false;
            return;
        }
        _loop.TrackRuntimeDetached("cast movement cancellation", () => CancelCastAsync(s, SkillInterruptedInfo.Types.SkillInterruptType.Move));
    }
}
