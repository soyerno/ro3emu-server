using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Invented content: cancelling a skill's action does not let the same skill start again before that action would have
/// ended. Interrupting it (31124, with or without its NxtSkill), walking away from a skill that locks movement
/// (_bMovement 0) and cutting it with another skill all leave its group busy (3005) until then, less the slack.
/// </summary>
public sealed partial class SkillRecastTests
{
    [Fact]
    public async Task AnInterruptWithTheSameSkillNextDoesNotRestartItsAction()
    {
        await using var bot = await Start();
        Assert.Equal(0, await Cast(bot, Dust));
        var (_, end) = await Active();

        await bot.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq
        {
            InterruptSkillId = Dust, NxtSkill = new CastSkillReq { SkillId = Dust, TargetId = Session.Role!.Id },
        });
        Assert.True(await Interrupted(bot));
        Assert.Equal(ErrorCodes.SkillBusy, await Result(bot));
        await AcceptedOnceTheActionEnds(bot, Dust, end);
    }

    [Fact]
    public async Task AnInterruptDoesNotFreeTheSkillForTheNextCast()
    {
        await using var bot = await Start();
        Assert.Equal(0, await Cast(bot, Dust));
        var (_, end) = await Active();

        await bot.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq { InterruptSkillId = Dust });
        Assert.True(await Interrupted(bot));
        Assert.Equal(ErrorCodes.SkillBusy, await Cast(bot, Dust));
        await AcceptedOnceTheActionEnds(bot, Dust, end);
    }

    [Fact]
    public async Task WalkingAwayDoesNotFreeTheSkillForTheNextCast()
    {
        await using var bot = await Start();
        Assert.Equal(0, await Cast(bot, Dust));
        var (_, end) = await Active();

        await Loop.InvokeAsync(() =>
        {
            Session.Position.X += 1;
            Session.Walking = true;
            Host.Service<CombatService>().PlayerMoved(Session);
            return Task.CompletedTask;
        });
        await Until(() => Session.SkillCast == null);
        Assert.Equal(ErrorCodes.SkillBusy, await Cast(bot, Dust));
        await AcceptedOnceTheActionEnds(bot, Dust, end);
    }

    [Fact]
    public async Task CuttingTheActionWithAnotherSkillDoesNotFreeTheFirstSkill()
    {
        await using var bot = await Start();
        Assert.Equal(0, await Cast(bot, Dust));
        var (_, end) = await Active();

        Assert.Equal(0, await Cast(bot, Spark));
        Assert.Equal(ErrorCodes.SkillBusy, await Cast(bot, Dust));
        await AcceptedOnceTheActionEnds(bot, Dust, end);
    }

    /// <summary>Just inside the slack before the cancelled action would have ended, the skill is taken again.</summary>
    private async Task AcceptedOnceTheActionEnds(BotClient bot, int skill, long end)
    {
        long wait = end - CombatService.SwingJitterMs + 40 - MonsterService.NowMs;
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
        Assert.True(MonsterService.NowMs < end, "the test ran past the action; nothing left to check");
        Assert.Equal(0, await Cast(bot, skill));
    }

    private static async Task<bool> Interrupted(BotClient bot) => SkillInterruptResp.Parser.ParseFrom(
        (await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.SkillInterrupt), Wait)).Body).IsInterruptSucc;

    private static async Task<int> Result(BotClient bot) => CastSkillRsp.Parser.ParseFrom(
        (await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.UseSkill), Wait)).Body).Result;

    private async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await Loop.InvokeAsync(() => Task.FromResult(condition())))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the condition did not hold in time");
            await Task.Delay(10);
        }
    }
}
