using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Invented content: a skill that may cut another skill's action (_bCancelCast 1) and has no cooldown cannot cut and
/// restart its own action by being spammed; it waits for its action like a normal attack waits for its swing (issue #121),
/// with the same CombatService.SwingJitterMs of slack. Another skill still cuts the action. The wait outlives the cast:
/// interrupting it (31124), walking away or cutting it with another skill does not free the skill early.
/// </summary>
public sealed partial class SkillRecastTests : IAsyncDisposable
{
    private const int Dust = 99201, Spark = 99301, Hymn = 99401;
    private const int ActionMs = 1200, ChantMs = 1500;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-recast-" + Guid.NewGuid().ToString("N"));
    private SyntheticHost? _host;

    private SyntheticHost Host => _host ?? throw new InvalidOperationException("Start the host first.");
    private GameSession Session => Host.Service<SessionRegistry>().All().Single(s => s.Role != null);
    private GameLoop Loop => Host.Service<GameLoop>();

    [Fact]
    public async Task ASkillWaitsForItsOwnActionBeforeItIsCastAgain()
    {
        await using var bot = await Start();
        Assert.Equal(0, await Cast(bot, Dust));
        var (id, end) = await Active();

        // A second press in the middle of the action is refused and leaves the action running.
        int sent = Interruptions(bot);
        Assert.Equal(ErrorCodes.SkillBusy, await Cast(bot, Dust));
        Assert.Equal(sent, Interruptions(bot));
        Assert.Equal((id, end), await Active());

        // Just inside the slack before the action ends, the next one is taken.
        long wait = end - CombatService.SwingJitterMs + 40 - MonsterService.NowMs;
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
        Assert.True(MonsterService.NowMs < end, "the test ran past the action; nothing left to check");
        Assert.Equal(0, await Cast(bot, Dust));
    }

    [Fact]
    public async Task AnotherSkillStillCutsTheActionAndTheSkillIsFreeOnceItsActionEnds()
    {
        await using var bot = await Start();
        Assert.Equal(0, await Cast(bot, Dust));
        Assert.Equal(0, await Cast(bot, Spark));
        var (_, end) = await Active();

        long wait = end + 20 - MonsterService.NowMs;
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
        Assert.Equal(0, await Cast(bot, Spark));
    }

    private async Task<BotClient> Start()
    {
        foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(_root, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        File.WriteAllText(Path.Combine(_root, "config", "SkillConfig.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [Dust.ToString(CultureInfo.InvariantCulture)] = Skill(Dust, 992),
            [Spark.ToString(CultureInfo.InvariantCulture)] = Skill(Spark, 993),
            [Hymn.ToString(CultureInfo.InvariantCulture)] = Chanted(Hymn, 994),
        }));
        _host = await SyntheticHost.StartAsync(o => o.GameDataDir = _root);
        return await Host.InWorldAsync("Recaster");
    }

    /// <summary>An active self-centred skill (_iRelease 0) with no cooldown, no cost and a fixed action time.</summary>
    private static object Skill(int id, int group) => new
    {
        _iID = id, _iSkillID = group, _iBasicType = 1, _bCancelCast = 1, _iCD = 0, _iActionTime = ActionMs,
    };

    /// <summary>The same skill with a fixed chant before its action.</summary>
    private static object Chanted(int id, int group) => new
    {
        _iID = id, _iSkillID = group, _iBasicType = 1, _bCancelCast = 1, _iCD = 0, _iActionTime = ActionMs,
        _iChantFix = ChantMs,
    };

    private async Task<int> Cast(BotClient bot, int skill)
    {
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = Session.Role!.Id });
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.UseSkill), Wait);
        return CastSkillRsp.Parser.ParseFrom(packet.Body).Result;
    }

    private Task<(long Id, long End)> Active() => Loop.InvokeAsync(() =>
    {
        var cast = Session.SkillCast ?? throw new InvalidOperationException("No active cast.");
        Assert.False(cast.Cancelled);
        return Task.FromResult((cast.Id, cast.StartMs + cast.ChantMs + cast.ActionMs));
    });

    private static int Interruptions(BotClient bot) => bot.Inbox.Snapshot().Count(e => e.IsGame(CombatService.SkillEnd)
        && SkillEndNotify.Parser.ParseFrom(e.Body).Type == SkillEndNotify.Types.SkillEndType.Interrupted);

    public async ValueTask DisposeAsync()
    {
        if (_host != null) await _host.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
