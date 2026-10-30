using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>ServerOptions.PartyExpShare (inferred operator option): a kill's Base EXP split evenly among the killer's
/// party members on the same map; off keeps it with the killer.</summary>
public sealed class PartyExpShareTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const long KillExp = 5, KillJobExp = 4;

    [Fact]
    public async Task OffKeepsTheWholeKillWithTheKiller()
    {
        await using var host = await SyntheticHost.StartAsync();
        var (alice, bob) = await PartyAsync(host);
        await using var a = alice;
        await using var b = bob;

        await KillAsync(host, "alice");

        Assert.Equal((KillExp, KillJobExp), await ExpAsync(host, "alice"));
        Assert.Equal((0L, 0L), await ExpAsync(host, "bob"));
    }

    [Fact]
    public async Task OnSplitsBaseExpEvenlyOnTheSameMap()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.PartyExpShare = true);
        var (alice, bob) = await PartyAsync(host);
        await using var a = alice;
        await using var b = bob;

        await KillAsync(host, "alice");

        // 5 / 2 = 2 each; Job EXP stays with the killer.
        Assert.Equal((KillExp / 2, KillJobExp), await ExpAsync(host, "alice"));
        Assert.Equal((KillExp / 2, 0L), await ExpAsync(host, "bob"));
    }

    [Fact]
    public async Task OnSkipsMembersOnAnotherMap()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.PartyExpShare = true);
        var (alice, bob) = await PartyAsync(host);
        await using var a = alice;
        await using var b = bob;
        await host.Service<GameLoop>().InvokeAsync(() =>
        {
            Session(host, "bob").LineNumber = 2; // another line of the map is another WorldScene
            return Task.CompletedTask;
        });

        await KillAsync(host, "alice");

        Assert.Equal((KillExp, KillJobExp), await ExpAsync(host, "alice"));
        Assert.Equal((0L, 0L), await ExpAsync(host, "bob"));
    }

    private static GameSession Session(SyntheticHost host, string account) =>
        host.Service<SessionRegistry>().All().Single(s => s.Account == account && s.Role != null);

    private static Task<(long Base, long Job)> ExpAsync(SyntheticHost host, string account) =>
        host.Service<GameLoop>().InvokeAsync(() =>
        {
            var p = Session(host, account).Progress!;
            return Task.FromResult((p.Exp, p.JobExp));
        });

    /// <summary>Alice creates a party and Bob joins it (TeamCreate, TeamInvite, TeamInviteeResp).</summary>
    private static async Task<(BotClient Alice, BotClient Bob)> PartyAsync(SyntheticHost host)
    {
        var alice = await host.InWorldAsync("Alice");
        var bob = await host.InWorldAsync("Bob");
        long aliceId = Session(host, "alice").Role!.Id, bobId = Session(host, "bob").Role!.Id;
        await alice.SendGameAsync(TeamService.TeamCreate, new Romsg.TeamCreateReq { TeamType = Romsg.TeamType.Five, TargetId = 1001 });
        var created = await Next(alice, TeamService.TeamCreate, Romsg.TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        await alice.SendGameAsync(TeamService.TeamInvite, new Romsg.InviteIntoTeamReq { Guid = bobId });
        Assert.Equal(0, (await Next(alice, TeamService.TeamInvite, Romsg.InviteIntoTeamResp.Parser)).Result);
        await Next(bob, TeamService.TeamNotifyInvitee, Romsg.NotifyInvitee.Parser);
        await bob.SendGameAsync(TeamService.TeamInviteeResp, new Romsg.InviteeResp { TeamId = created.TeamId, IsAgree = true, InviteGuid = aliceId });
        Assert.Equal(0, (await Next(bob, TeamService.TeamInviteeResp, Romsg.InviteeRespResp.Parser)).Result);
        return (alice, bob);
    }

    /// <summary>One kill by <paramref name="account"/> worth <see cref="KillExp"/> Base and <see cref="KillJobExp"/> Job
    /// EXP and no items, through the real reward commit and publication.</summary>
    private static Task KillAsync(SyntheticHost host, string account) => host.Service<GameLoop>().InvokeAsync(async () =>
    {
        var rewards = host.Service<RewardService>();
        rewards.ResolveKillForTest = (_, _) => ([], KillExp, KillJobExp);
        var killer = Session(host, account);
        var monster = new MonsterUnit
        {
            Uid = 77, SceneEntry = killer.SceneEntry, RuntimeMap = killer.RuntimeMap,
            Template = new MonsterTemplate(10001, 1, 10, 0, 1, 1, 0, 0, 1, 0, 0, 100, 1, 1, 1, 1,
                false, 0, 1000, 5, 10, 0, 1000, .1f, 1000, 1, 500),
            Group = new SpawnGroup(-1, false, 1, 0, [], []), Home = new SpawnPoint(-1, 0, 0, 0, 0, 10001), Hp = 0
        };
        await host.Service<CombatService>().Killed.Single(h => h.Target == rewards)(killer, monster);
    });

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T>
        => parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(msgId), Wait)).Body);
}
