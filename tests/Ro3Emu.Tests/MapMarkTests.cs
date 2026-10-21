using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Big-map flags in a battle instance (#171): 30407 places and deletes, 30408 sends the type's whole list to
/// the team in the same instance, 30431 gathers at a flag. Runs on the fixture.</summary>
public sealed class MapMarkTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private const long Instance = 900001;

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private static DungeonMarkPos Flag(DungeonMarkType type, int id = 0, float x = 1) =>
        new() { Id = id, MarkType = type, Pos = new Vec3 { X = x, Y = 2, Z = 3 } };

    private static async Task<DungeonSetMarkPosRsp> Set(BotClient bot, bool del, DungeonMarkPos pos)
    {
        await bot.SendGameAsync(MapMarkService.SetMark, new DungeonSetMarkPosReq { BDel = del, Pos = pos });
        return await Next(bot, MapMarkService.SetMark, DungeonSetMarkPosRsp.Parser);
    }

    private static async Task<DungeonSetMarkPosNtf> NextList(BotClient bot) =>
        await Next(bot, MapMarkService.MarkNtf, DungeonSetMarkPosNtf.Parser);

    private static async Task Place(SyntheticHost host, long instance, params BotClient[] bots)
    {
        var sessions = host.Service<SessionRegistry>();
        await host.Service<GameLoop>().InvokeAsync(() =>
        {
            foreach (var bot in bots)
                sessions.All().Single(s => s.Role?.Id == (long)bot.Self!.Uuid).InstanceId = instance;
            return Task.CompletedTask;
        });
    }

    private static async Task<(BotClient Alice, BotClient Bob)> RaidAsync(SyntheticHost host)
    {
        var alice = await host.InWorldAsync("Alice");
        var bob = await host.InWorldAsync("Bob");
        await alice.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Forty, TargetId = 1001 });
        var created = await Next(alice, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)bob.Self!.Uuid });
        Assert.Equal(0, (await Next(alice, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        await Next(bob, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await bob.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
        { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)alice.Self!.Uuid });
        Assert.Equal(0, (await Next(bob, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
        await Place(host, Instance, alice, bob);
        return (alice, bob);
    }

    [Fact]
    public async Task TheRaidLeaderPlacesAndDeletesFlagsInFreeSlotsAndTheTeamSeesTheList()
    {
        await using var host = await SyntheticHost.StartAsync();
        var (alice, bob) = await RaidAsync(host);
        await using var a = alice;
        await using var b = bob;
        var leader = DungeonMarkType.DungeonMarkTeamLeader;

        var first = await Set(alice, false, Flag(leader));
        Assert.Equal(0, first.Result);
        Assert.Equal(1, first.Pos.Id);
        Assert.Equal(new[] { 1 }, (await NextList(bob)).Pos.Select(p => p.Id));
        Assert.Equal(2, (await Set(alice, false, Flag(leader, x: 5))).Pos.Id);
        Assert.Equal(new[] { 1, 2 }, (await NextList(bob)).Pos.Select(p => p.Id));

        // Deleting slot 1 frees it for the next flag; every push carries the type's whole list.
        Assert.Equal(0, (await Set(alice, true, Flag(leader, id: 1))).Result);
        var after = await NextList(bob);
        Assert.Equal(leader, after.MarkType);
        Assert.Equal(new[] { 2 }, after.Pos.Select(p => p.Id));
        Assert.Equal(5f, after.Pos[0].Pos.X);
        Assert.Equal(1, (await Set(alice, false, Flag(leader))).Pos.Id);
        Assert.Equal(ErrorCodes.Parameter, (await Set(alice, true, Flag(leader, id: 9))).Result);
    }

    [Fact]
    public async Task OnlyTheRightPeopleInABattleInstanceMayManageFlags()
    {
        await using var host = await SyntheticHost.StartAsync();
        var (alice, bob) = await RaidAsync(host);
        await using var a = alice;
        await using var b = bob;

        // A team member isn't the raid leader, and nobody here holds the guild's battlefield command right.
        Assert.Equal(ErrorCodes.NoPermission, (await Set(bob, false, Flag(DungeonMarkType.DungeonMarkTeamLeader))).Result);
        Assert.Equal(ErrorCodes.NoPermission, (await Set(alice, false, Flag(DungeonMarkType.DungeonMarkCommander))).Result);
        Assert.Equal(ErrorCodes.Parameter, (await Set(alice, false, Flag(DungeonMarkType.DungeonMarkNone))).Result);

        // The fixture has no GVEMapMarkMaxNum, so the client's 10 slots apply.
        for (int i = 1; i <= 10; i++)
            Assert.Equal(i, (await Set(alice, false, Flag(DungeonMarkType.DungeonMarkTeamLeader))).Pos.Id);
        Assert.Equal(ErrorCodes.Parameter, (await Set(alice, false, Flag(DungeonMarkType.DungeonMarkTeamLeader))).Result);

        await Place(host, 0, alice);
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Set(alice, false, Flag(DungeonMarkType.DungeonMarkTeamLeader))).Result);
    }

    [Fact]
    public async Task AGatherAtAFlagReachesTheTeamInTheSameInstanceOnly()
    {
        await using var host = await SyntheticHost.StartAsync();
        var (alice, bob) = await RaidAsync(host);
        await using var a = alice;
        await using var b = bob;
        var leader = DungeonMarkType.DungeonMarkTeamLeader;
        var flag = (await Set(alice, false, Flag(leader))).Pos;

        await alice.SendGameAsync(MapMarkService.Gather, new DungeonGatherMarkPosReq
        { GatherType = DungeonGatherType.DungeonGatherMark, Pos = flag });
        Assert.Equal(0, (await Next(alice, MapMarkService.Gather, DungeonGatherMarkPosRsp.Parser)).Result);
        var gather = await Next(bob, MapMarkService.GatherNtf, DungeonGatherMarkPosNtf.Parser);
        Assert.Equal(flag.Id, gather.Pos.Id);

        await alice.SendGameAsync(MapMarkService.Gather, new DungeonGatherMarkPosReq
        { GatherType = DungeonGatherType.DungeonGatherMark, Pos = Flag(leader, id: 7) });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, MapMarkService.Gather, DungeonGatherMarkPosRsp.Parser)).Result);

        // Bob leaves for another instance: the next gather isn't sent to him.
        await Place(host, Instance + 1, bob);
        int Gathers() => bob.Inbox.Snapshot().Count(e => e.IsGame(MapMarkService.GatherNtf));
        int before = Gathers();
        await alice.SendGameAsync(MapMarkService.Gather, new DungeonGatherMarkPosReq
        { GatherType = DungeonGatherType.DungeonGatherMark, Pos = flag });
        Assert.Equal(0, (await Next(alice, MapMarkService.Gather, DungeonGatherMarkPosRsp.Parser)).Result);
        await Next(alice, MapMarkService.GatherNtf, DungeonGatherMarkPosNtf.Parser);
        Assert.Equal(before, Gathers());
    }
}
