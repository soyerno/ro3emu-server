using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The Wild AFK GM aids (!afkunlock, !afktime, !afkmode): not original behavior, but they must keep the service's own
/// rules (persist first, then memory, then 5035). The synthetic data set gets an invented FunctionConfig row 18 whose
/// open condition does not exist, so a new role starts with auto mode locked.
/// </summary>
public sealed class WildAfkGmTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _gameData = Path.Combine(Path.GetTempPath(), "ro3emu-afkgm-data-" + Guid.NewGuid().ToString("N"));
    private SyntheticHost _host = null!;

    public async Task InitializeAsync()
    {
        LockedFixture(_gameData);
        _host = await SyntheticHost.StartAsync(o => o.GameDataDir = _gameData);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_gameData, true); } catch (IOException) { }
    }

    /// <summary>A copy of the synthetic data set with FunctionConfig 18 behind a condition that no table defines.</summary>
    private static void LockedFixture(string target)
    {
        foreach (string file in Directory.GetFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string copy = Path.Combine(target, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        string functions = Path.Combine(target, "config", "FunctionConfig.json");
        var table = JsonNode.Parse(File.ReadAllText(functions))!.AsObject();
        table["18"] = new JsonObject { ["_iID"] = 18, ["_kOpenCondition"] = 99999018 };
        File.WriteAllText(functions, table.ToJsonString());
    }

    private T Service<T>() where T : notnull => _host.Service<T>();
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private Task<T> OnLoop<T>(BotClient bot, Func<GameSession, T> read) =>
        Service<GameLoop>().InvokeAsync(() => Task.FromResult(read(Session(bot))));
    private Task<bool> Open(BotClient bot) => OnLoop(bot, s => Service<FunctionService>().IsOpen(s, WildAfkService.Function));

    private WildAfkService.State Stored(BotClient bot) =>
        JsonSerializer.Deserialize<WildAfkService.State>(Service<RoleStore>().LoadWildAfk((long)bot.Self!.Uuid)!)!;

    /// <summary>Runs a GM line through the loopback relay and returns its marquee reply.</summary>
    private async Task<string> Gm(BotClient bot, string line, string expect)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_host.Http}/ro3emu/gm?account={bot.Account}", new StringContent(line))).EnsureSuccessStatusCode();
        return await Marquee(bot, expect);
    }

    private static async Task<string> Marquee(BotClient bot, string contains) =>
        BroadCastNotify.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify)
            && BroadCastNotify.Parser.ParseFrom(e.Body).Content.Contains(contains, StringComparison.Ordinal), Wait)).Body).Content;

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool> match) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id) && match(parser.ParseFrom(e.Body)), Wait)).Body);

    [Fact]
    public async Task UnlockOpensALockedRoleAndSurvivesARestartUntilTurnedOff()
    {
        await using (var alice = await _host.InWorldAsync("Alice"))
        {
            Assert.False(await Open(alice));
            await Gm(alice, "!afkunlock on", "unlock");
            Assert.True(await Open(alice));
            // 5056 tells the client the function opened, so the OnHook panel shows.
            await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser,
                m => m.FunctionOpenInfos.ContainsKey(WildAfkService.Function));
            Assert.True(Stored(alice).GmUnlocked);
        }

        await _host.RestartAsync();
        await using var again = await _host.InWorldAsync("Alice", create: false);
        Assert.True(await Open(again));
        Assert.Contains("GM override on", await Gm(again, "!afktime info", "min"));

        await Gm(again, "!afkunlock off", "unlock");
        Assert.False(await Open(again));
        Assert.False(Stored(again).GmUnlocked);
    }

    [Fact]
    public async Task AfkTimeSetAndAddPersistAndClampAtZero()
    {
        await using (var alice = await _host.InWorldAsync("Alice"))
        {
            await Gm(alice, "!afktime set 90", "(5400 s)");
            Assert.Equal(5400, await OnLoop(alice, s => s.WildAfk!.Info.HighMultiplierTimeSeconds));
            Assert.Equal(5400, Stored(alice).Info.HighMultiplierTimeSeconds);

            await Gm(alice, "!afktime add -200", "(0 s)");
            Assert.Equal(0, Stored(alice).Info.HighMultiplierTimeSeconds);
            Assert.False(await OnLoop(alice, s => s.WildAfk!.Info.IsHighMagnification)); // an empty bank switches off

            await Gm(alice, "!afktime add 30", "(1800 s)");
            Assert.Equal(1800, Stored(alice).Info.HighMultiplierTimeSeconds);
            await Gm(alice, "!afktime set -5", "(0 s)");
            Assert.Equal(0, await OnLoop(alice, s => s.WildAfk!.Info.HighMultiplierTimeSeconds));
        }

        await _host.RestartAsync();
        await using var again = await _host.InWorldAsync("Alice", create: false);
        Assert.Equal(0, await OnLoop(again, s => s.WildAfk!.Info.HighMultiplierTimeSeconds));
        string info = await Gm(again, "!afktime info", "min");
        Assert.Contains("(0 s)", info);
        Assert.Contains("locked", info);
    }

    [Fact]
    public async Task AfkModeOnIsRefusedWithAnEmptyBankAndPersistsOtherwise()
    {
        await using var alice = await _host.InWorldAsync("Alice");
        await Gm(alice, "!afktime set 0", "(0 s)");
        Assert.Contains("empty", await Gm(alice, "!afkmode on", "Bonus Time"));
        Assert.False(await OnLoop(alice, s => s.WildAfk!.Info.IsHighMagnification));

        await Gm(alice, "!afktime set 10", "(600 s)");
        await Gm(alice, "!afkmode on", "switch ON");
        Assert.True(await OnLoop(alice, s => s.WildAfk!.Info.IsHighMagnification));
        Assert.True(Stored(alice).Info.IsHighMagnification);

        await Gm(alice, "!afkmode off", "switch OFF"); // no client cooldown for the GM aid
        Assert.False(Stored(alice).Info.IsHighMagnification);
        Assert.Equal(600, Stored(alice).Info.HighMultiplierTimeSeconds);
    }
}
