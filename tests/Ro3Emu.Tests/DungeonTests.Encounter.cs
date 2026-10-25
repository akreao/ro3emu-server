using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using static Ro3Emu.Server.Game.ReconstructedDungeons;

namespace Ro3Emu.Tests;

/// <summary>Story boss encounters (Reconstructed/story-encounters.json, DungeonService.Encounter).</summary>
public sealed partial class DungeonTests
{
    private const long Eddga = 1022001;
    private const int EddgaNpc = 5300001, Wule = 5300081, Boya = 5300080;

    [ClientDataFact]
    public void EveryStoryEncounterNamesClientRows()
    {
        var data = Service<GameData>();
        var monsters = Service<MonsterService>();
        var catalog = Service<DungeonCatalog>();
        var encounters = Service<ReconstructedDungeons>().AllEncounters;
        Assert.NotEmpty(encounters);
        foreach (var e in encounters)
        {
            Assert.NotNull(monsters.Template(e.Boss));
            Assert.All(e.BossSkills, s => Assert.NotNull(data.Row("SkillConfig", s.Skill)));
            foreach (var (npc, kit) in e.Kits)
            {
                Assert.NotNull(monsters.Template(npc));
                Assert.All(kit.Skills, id => Assert.True(data.Row("SkillConfig", id) != null, $"{npc}: skill {id}"));
            }
            var present = new HashSet<int> { e.Boss };
            var lines = e.BossSkills.Where(s => s.Line != 0).Select(s => new EncounterLine(s.Line, 0, 0)).Concat(e.WinLines).ToList();
            foreach (var stage in e.Stages)
            {
                Assert.True(stage.Quest == 0 || data.Table("DungeonQuestConfig").Values.Any(q =>
                    GameData.Int(q, "_iDungeonID") == e.Scene && GameData.Int(q, "_iQuestID") == stage.Quest), $"quest {stage.Quest}");
                foreach (var ally in stage.Allies)
                {
                    Assert.True(e.Kits.ContainsKey(ally.NpcId), $"{ally.NpcId} has no kit");
                    Assert.True(catalog.ScenePoint(e.Scene, ally.Point) != null, $"point {ally.Point}");
                    present.Add(ally.NpcId);
                }
                foreach (var line in stage.Lines.Concat(stage.EndLines))
                {
                    var chat = data.Row("ChatConfig", line.Chat);
                    Assert.True(chat != null, $"chat {line.Chat}");
                    // A bubble needs its speaker on the field by then.
                    if (GameData.Int(chat.Value, "_iType") == 2) Assert.True(present.Contains(line.By), $"chat {line.Chat} by {line.By}");
                }
            }
            Assert.All(lines, l => Assert.True(data.Row("ChatConfig", l.Chat) != null, $"chat {l.Chat}"));
            foreach (var skill in e.BossSkills)
            {
                Assert.True(skill.Hits.Count > 0 || skill.Marks.Count > 0, $"{skill.Skill} hits nothing");
                foreach (var (mark, i) in skill.Marks.Select((m, i) => (m, i)))
                {
                    Assert.True(EffectData.Warning(data, mark.Warn) is { DurationMs: > 0 }, $"{skill.Skill}: warning {mark.Warn}");
                    Assert.True(mark.Show == 0 || EffectData.Exists(data, mark.Show), $"{skill.Skill}: effect {mark.Show}");
                    Assert.True(mark.Around < i, $"{skill.Skill}: mark {i} is placed around a later mark");
                }
                Assert.All(skill.Moves, m => Assert.InRange(m.To, 0, skill.Marks.Count - 1));
                Assert.True(skill.Moves.Count == 0 || EffectData.MovementMs(data, skill.MoveEffect) > 0, $"{skill.Skill}: move effect");
            }
        }
    }

    [Fact]
    public void EncounterHitAreasFollowTheBossFacing()
    {
        (float, float, float) facingPlusZ = (0, 0, 0), facingPlusX = (0, 0, 90);
        var circle = new EncounterHit(0, HitShape.Circle, 5, 0, 0, 0, 4, 10000);
        Assert.True(DungeonService.InArea(circle, facingPlusZ, 0, 9));    // 4 m ahead + 5 m
        Assert.False(DungeonService.InArea(circle, facingPlusZ, 0, -2.5f));
        Assert.True(DungeonService.InArea(circle, facingPlusX, 9, 0));
        var cone = new EncounterHit(0, HitShape.Cone, 6, 120, 0, 0, 0, 10000);
        Assert.True(DungeonService.InArea(cone, facingPlusZ, 2, 3));      // ~34 degrees off
        Assert.False(DungeonService.InArea(cone, facingPlusZ, 3, -1));    // behind
        Assert.False(DungeonService.InArea(cone, facingPlusZ, 0, 7));     // too far
        var rect = new EncounterHit(0, HitShape.Rect, 0, 0, 12, 4, 0, 10000);
        Assert.True(DungeonService.InArea(rect, facingPlusX, 11, 1.5f));
        Assert.False(DungeonService.InArea(rect, facingPlusX, 11, 3));
        Assert.False(DungeonService.InArea(rect, facingPlusZ, 11, 0));    // beside, not ahead
    }

    [ClientDataFact]
    public async Task EddgaFightPlaysAsTheGenesisCaptureShowsIt()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 60"); // Eddga's skills should not end the run before the companions do their part
        await Gm(alice, "!quest accept 10102200");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = Eddga }));
        long instance = await Load(alice, Eddga);
        var boss = await BossIn(instance, EddgaNpc);
        // 830 x 26.052 = 21623.16: Genesis showed 21624 (rounded up), this server rounds to the nearest.
        Assert.InRange(boss.Template.MaxHp, 21623, 21624);
        var boya = await BossIn(instance, Boya);
        await BossIn(instance, Wule);
        Assert.Equal(1, boss.HpFloor); // he cannot fall before the adventurers arrive
        await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            info => info.Vars.Any(v => v.VarId == DungeonService.AirWallVar && v.Vi64.SequenceEqual([32L])));

        // Stage 1 "Cover Boya and Wule": the narrator's line, then Boya's bubble (1022 with her uid).
        await Next(alice, DungeonService.Event, DungeonEventNotify.Parser,
            e => e.CliFuncname == "Shouting" && e.Params[0].IntParams.Single() == 5300019);
        await Next(alice, 1022, SystemBoardCastMsgNtf.Parser, m => m.ChatId == 5300018 && m.PlayerId == boya.Uid);

        // The stage ends at 15 s (moved forward here): black screen, quest done, Eddga's self-buff a second later.
        await OnLoop(() => { Session(alice).DungeonAttempt!.Encounter!.StageStartMs -= 15_000; return Task.CompletedTask; });
        var dark = await Next(alice, DungeonService.Event, DungeonEventNotify.Parser, e => e.CliFuncname == "BlackScreenMs");
        Assert.Equal(1000, dark.Params[0].IntParams.Single());
        await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            info => info.Vars.Any(v => v.VarId == 10003 && v.Vi64.SequenceEqual([1L, 1L])));
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            b => b.UnitId == boss.Uid && b.Buffs.Any(x => x.ConfigId == 5301001));

        // Stage 2: the swordman and priest come with it, the other ten after them.
        await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            info => info.Vars.Any(v => v.VarId == 10001 && v.Vi64.LastOrDefault() == 2));
        Assert.Equal(0, boss.HpFloor); // the last stage can be won
        Assert.Equal(2, (await Party(instance)).Length);
        await Next(alice, 1022, SystemBoardCastMsgNtf.Parser, m => m.ChatId == 5300030); // the mage's line, 7 s in
        var party = await Party(instance);
        Assert.Equal(12, party.Length);
        var uids = party.Select(m => m.Uid).ToHashSet();
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => uids.Contains(hit.SrcID) && hit.Dsts.Any(d => d.DstID == boss.Uid && d.Damage > 0));

        // A companion's last hit settles the run as the player's win; the closing lines come 6 s and 9 s later.
        await OnLoop(() => { boss.Hp = 1; return Task.CompletedTask; });
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        Assert.True(boss.Dead);
        await Next(alice, DungeonService.Event, DungeonEventNotify.Parser,
            e => e.CliFuncname == "Shouting" && e.Params[0].IntParams.Single() == 5300012);

        // The won story run sends the player back out about 3 s after its last line, without a countdown (Genesis).
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry != Eddga); // back where the run began
    }

    private Task<MonsterUnit[]> Party(long instance) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
        .OfType<MonsterUnit>().Where(m => m.InstanceId == instance && m.Template.NpcId is >= 5300007 and <= 5300009 or >= 5300070 and <= 5300078)
        .ToArray()));

    [ClientDataFact]
    public async Task EddgaMarksTheGroundWhereHisSkillsLand()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 60");
        await Gm(alice, "!quest accept 10102200");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = Eddga }));
        long instance = await Load(alice, Eddga);
        var boss = await BossIn(instance, EddgaNpc);
        var dungeons = Service<DungeonService>();

        // The jump: an 8 m ring 14.4 m ahead (Genesis), then eight rings 15 m around it that erupt a second later.
        Assert.True(await Service<GameLoop>().InvokeAsync(() => dungeons.CastEncounterSkillAsync(Session(alice).DungeonAttempt!, 5300501)));
        var centre = await Next(alice, CombatService.SkillEffectHigh, SkillEffectDisplayNotify.Parser, e => e.EffectId == 5300504);
        Assert.Equal(6, centre.EffectType);
        Assert.Equal(boss.Uid, centre.SourceId);
        Assert.InRange(MonsterService.Distance(boss.X, boss.Z, centre.TargetPos.X, centre.TargetPos.Z), 14.3f, 14.5f);
        var ring = await Next(alice, CombatService.SkillEffectHigh, SkillEffectDisplayNotify.Parser, e => e.EffectId == 5300505);
        Assert.InRange(MonsterService.Distance(centre.TargetPos.X, centre.TargetPos.Z, ring.TargetPos.X, ring.TargetPos.Z), 14.9f, 15.1f);
        await Next(alice, CombatService.SkillEffectHigh, SkillEffectDisplayNotify.Parser, e => e.EffectId == 5300513 && e.SourceId == 0);

        // 801 in stage 2: a strip at a fighter and a dash onto it, then the ring with its eight strips.
        Assert.True(await Service<GameLoop>().InvokeAsync(() => dungeons.SkipEncounterStageAsync(Session(alice).DungeonAttempt!)));
        Assert.True(await Service<GameLoop>().InvokeAsync(() => dungeons.CastEncounterSkillAsync(Session(alice).DungeonAttempt!, 5300801)));
        await Next(alice, CombatService.SkillEffectHigh, SkillEffectDisplayNotify.Parser, e => e.EffectId == 5300841);
        var dash = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.SkillConfigId == 5300801);
        Assert.Equal(5300851, dash.EffectId);
        Assert.Equal(600, dash.MoveTimeMs);
        var big = await Next(alice, CombatService.SkillEffectHigh, SkillEffectDisplayNotify.Parser, e => e.EffectId == 5300844);
        for (int i = 0; i < 8; i++)
        {
            var strip = await Next(alice, CombatService.SkillEffectHigh, SkillEffectDisplayNotify.Parser, e => e.EffectId == 5300843);
            Assert.InRange(MonsterService.Distance(big.TargetPos.X, big.TargetPos.Z, strip.TargetPos.X, strip.TargetPos.Z), 9.9f, 10.1f);
        }
    }
}
