using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;
using static Ro3Emu.Server.Game.ReconstructedDungeons;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A story boss skill's ground marks (ReconstructedDungeons.Marks), sent the way the Genesis capture of Payon Forest
/// (2026-10-25, one run; Genesis differs from the official beta) shows Eddga's: each mark is a 31109 from the boss with
/// effect type 6 and the warning effect at its position and angle, which the client draws from EffectData
/// m_kWarningRange; when it lands, a 31109 of the damage effect (types 4 and 2, no source) and the hit on everyone
/// inside the warning's shape. A target is hit once per moment however many marks cover it. Dashes are 31112 with the
/// skill's displacement effect.
/// </summary>
public sealed partial class DungeonService
{
    private const int WarningEffectType = 6;               // Genesis: the warning 31109
    private static readonly int[] DamageEffectTypes = [4, 2]; // Genesis: the damage effect 31109, sent twice
    private const int DefaultMoveMs = 600;

    private sealed record PlacedMark(float X, float Z, float Ref);

    private sealed class MarkCast(Attempt attempt, EncounterRun run, EncounterSkill skill, JsonElement row, long castId)
    {
        public Attempt Attempt { get; } = attempt;
        public EncounterRun Run { get; } = run;
        public EncounterSkill Skill { get; } = skill;
        public JsonElement Row { get; } = row;
        public long CastId { get; } = castId;
        public PlacedMark?[] Placed { get; } = new PlacedMark?[skill.Marks.Count];
        /// <summary>Hit moment (ms into the cast) -> who was already hit then.</summary>
        public Dictionary<int, HashSet<long>> Struck { get; } = [];
    }

    private void StartMarks(MarkCast c)
    {
        foreach (var group in c.Skill.Marks.Select((m, i) => (Mark: m, Index: i)).GroupBy(x => x.Mark.Ms))
            _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(group.Key), async () =>
            {
                foreach (var (mark, index) in group)
                    if (MarkCastLive(c)) await PlaceMarkAsync(c, mark, index);
            });
        foreach (var move in c.Skill.Moves)
            _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(move.Ms), () => MarkCastLive(c) ? DashAsync(c, move) : Task.CompletedTask);
    }

    private static bool MarkCastLive(MarkCast c) => !c.Run.Done && !c.Attempt.Settled && !c.Attempt.Room.Closed
        && !c.Run.Boss.Dead && c.Run.Boss.Hp > 0;

    private async Task PlaceMarkAsync(MarkCast c, EncounterMark mark, int index)
    {
        if (EffectData.Warning(_data, mark.Warn) is not { } range) return;
        var boss = c.Run.Boss;
        var anchor = mark.Around >= 0 && mark.Around < index ? c.Placed[mark.Around] : null;
        float reference = mark.World ? 0 : anchor?.Ref ?? await AimAsync(c, mark.Aim);
        float fromX = anchor?.X ?? boss.X, fromZ = anchor?.Z ?? boss.Z;
        var spots = new List<(float X, float Z, float Dir)>();
        for (int k = 0; k < Math.Max(1, mark.Count); k++)
        {
            double polar = (reference + mark.Angle + mark.Step * k) * Math.PI / 180;
            spots.Add((fromX + (float)Math.Sin(polar) * mark.Distance, fromZ + (float)Math.Cos(polar) * mark.Distance,
                (reference + mark.Dir + mark.Step * k) % 360));
        }
        c.Placed[index] = new PlacedMark(spots[0].X, spots[0].Z, reference);
        foreach (var spot in spots)
            await BroadcastAsync(c.Attempt, CombatService.SkillEffectHigh, MarkEffect(c, mark.Warn, spot, WarningEffectType, fromBoss: true));
        int hitMs = mark.HitMs > 0 ? mark.HitMs : range.DurationMs;
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(hitMs), () => MarkCastLive(c)
            ? StrikeMarksAsync(c, mark, range, spots, mark.Ms + hitMs) : Task.CompletedTask);
    }

    /// <summary>The direction a mark turns with; aiming at a unit turns the boss to it (Genesis turns Eddga each time).</summary>
    private async Task<float> AimAsync(MarkCast c, MarkAim aim)
    {
        var boss = c.Run.Boss;
        (float X, float Z)? at = aim switch
        {
            MarkAim.Target => boss.Target is { } p ? (p.Position.X, p.Position.Z) : boss.MonsterTarget is { } m ? (m.X, m.Z) : null,
            MarkAim.Random => RandomEnemy(c),
            _ => null,
        };
        if (at is not { } to || MonsterService.Distance(boss.X, boss.Z, to.X, to.Z) < 0.1f) return boss.Face;
        boss.Face = MonsterService.Angle(boss.X, boss.Z, to.X, to.Z);
        await _world.MoveUnitAsync(boss, new MovementChangeNtf { Uid = boss.Uid, MovementInfo = boss.CurrentMovement(MonsterService.NowMs) });
        return boss.Face;
    }

    /// <summary>A random player or fighting companion in the boss's scene (which one Genesis picks is not known: inferred).</summary>
    private (float X, float Z)? RandomEnemy(MarkCast c)
    {
        var boss = c.Run.Boss;
        var spots = Present(c.Attempt).Where(p => p.Role != null && !p.Dead && !p.SceneLoading && p.WorldScene == boss.WorldScene)
            .Select(p => (p.Position.X, p.Position.Z))
            .Concat(c.Run.Allies.Where(s => !s.Kit.Idle && !s.Unit.Dead).Select(s => (s.Unit.X, s.Unit.Z))).ToArray();
        return spots.Length == 0 ? null : spots[_encounterRandom.Next(spots.Length)];
    }

    private async Task StrikeMarksAsync(MarkCast c, EncounterMark mark, WarningRange range, List<(float X, float Z, float Dir)> spots,
        int moment)
    {
        var boss = c.Run.Boss;
        if (mark.Show > 0)
            foreach (var spot in spots)
                foreach (int type in DamageEffectTypes)
                    await BroadcastAsync(c.Attempt, CombatService.SkillEffectHigh, MarkEffect(c, mark.Show, spot, type, fromBoss: false));
        var area = range.Circle ? new EncounterHit(0, HitShape.Circle, range.Radius, 0, 0, 0, 0, 10000)
            : new EncounterHit(0, HitShape.Rect, 0, 0, range.Length, range.Width, 0, 10000);
        if (!c.Struck.TryGetValue(moment, out var struck)) c.Struck[moment] = struck = [];
        bool Hit(float x, float z, long id) => spots.Any(s => InArea(area, s, x, z)) && struck.Add(id);
        var players = Present(c.Attempt).Where(p => p.Role != null && !p.Dead && !p.SceneLoading && p.WorldScene == boss.WorldScene
            && Hit(p.Position.X, p.Position.Z, p.Role.Id)).ToArray();
        if (players.Length > 0) await _combat.MonsterSkillHitAsync(boss, c.Row, players, boss.Uid, session: c.CastId);
        foreach (var ally in c.Run.Allies.Where(s => !s.Kit.Idle).Select(s => s.Unit).Where(u => !u.Dead && Hit(u.X, u.Z, u.Uid)).ToArray())
            await _combat.MonsterDamagesMonsterAsync(boss, ally, c.Row, c.CastId);
    }

    private static SkillEffectDisplayNotify MarkEffect(MarkCast c, int effect, (float X, float Z, float Dir) at, int type, bool fromBoss) => new()
    {
        SourceId = fromBoss ? c.Run.Boss.Uid : 0, MasterId = c.Run.Boss.Uid, SkillId = c.Skill.Skill, EffectId = effect,
        EffectType = type, Angle = at.Dir, TargetPos = new Vec3 { X = at.X, Y = c.Run.Boss.Y, Z = at.Z },
    };

    /// <summary>The boss dashes toward a placed mark on the skill's displacement effect.</summary>
    private async Task DashAsync(MarkCast c, EncounterMove move)
    {
        if (move.To < 0 || move.To >= c.Placed.Length || c.Placed[move.To] is not { } to) return;
        var boss = c.Run.Boss;
        long now = MonsterService.NowMs;
        if (boss.Moving) { boss.Advance(now); boss.Moving = false; }
        float dx = to.X - boss.X, dz = to.Z - boss.Z, length = MathF.Sqrt(dx * dx + dz * dz);
        float go = move.Distance > 0 ? Math.Min(move.Distance, length) : length;
        if (go < 0.1f) return;
        var start = boss.Pos;
        boss.X += dx / length * go;
        boss.Z += dz / length * go;
        int ms = EffectData.MovementMs(_data, c.Skill.MoveEffect) is var t and > 0 ? t : DefaultMoveMs;
        await BroadcastAsync(c.Attempt, CombatService.SkillMove, new SkillStartMoveNotify
        {
            EntityID = (ulong)boss.Uid, StartPos = start, EndPos = boss.Pos, SkillConfigId = c.Skill.Skill, EffectId = c.Skill.MoveEffect,
            MoveType = 1, StartTimestampMs = now, Face = boss.Face, MoveTimeMs = ms,
        });
        await _world.MoveUnitAsync(boss, new MovementChangeNtf { Uid = boss.Uid, MovementInfo = boss.CurrentMovement(now) });
    }
}
