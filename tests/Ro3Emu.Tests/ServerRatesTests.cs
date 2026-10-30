using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Konsor server rates (not original behavior): the konsor_rate table of the realm database scales kill EXP, award EXP,
/// monster drop chances and kill-pool Zeny. All rates at 1.0, or no table at all, must leave every reward unchanged.
/// </summary>
public sealed class ServerRatesTests
{
    private static Dictionary<string, double> Rows(params (string Name, double Value)[] rows) =>
        rows.ToDictionary(r => r.Name, r => r.Value);

    [Fact]
    public void DefaultRatesLeaveEveryRewardUnchanged()
    {
        var rates = new ServerRates().Current;
        Assert.Equal(ServerRates.Values.Default, rates);
        foreach (long exp in new long[] { 0, 1, 7, 123_456, long.MaxValue / 2 })
            Assert.Equal((exp, exp), rates.KillExp(exp));
        foreach (long exp in new long[] { 0, 1, 99 })
            Assert.Equal(exp, rates.AwardExp(exp));
        foreach (double chance in new double[] { 0, 1, 333_333.5, 1_000_000, 2_000_000 })
            Assert.Equal(chance, rates.DropChance(chance));
        var drops = new List<(int, int)> { (1, 7), (1000000, 3) };
        rates.ScaleZeny(drops, 0);
        Assert.Equal([(1, 7), (1000000, 3)], drops);
    }

    [Fact]
    public void KillBaseAndJobExpScaleIndependently()
    {
        var rates = new ServerRates();
        rates.Apply(Rows(("base_exp", 2.0), ("job_exp", 0.5)));
        Assert.Equal((200L, 50L), rates.Current.KillExp(100));
        Assert.Equal((0L, 0L), rates.Current.KillExp(0));
    }

    [Fact]
    public void ScaledAmountsFloorWithAMinimumOfOne()
    {
        Assert.Equal(1, ServerRates.Scale(1L, 0.1));
        Assert.Equal(4, ServerRates.Scale(3L, 1.5));
        Assert.Equal(115, ServerRates.Scale(100L, 1.15)); // decimal math: 1.15 x 100 is not 114.999...
        Assert.Equal(0, ServerRates.Scale(0L, 10));
        Assert.Equal(long.MaxValue, ServerRates.Scale(long.MaxValue, 10));
        Assert.Equal(int.MaxValue, ServerRates.Scale(int.MaxValue, 10));
    }

    [Fact]
    public void AwardExpUsesTheQuestRateOnly()
    {
        var rates = new ServerRates();
        rates.Apply(Rows(("quest_exp", 3.0), ("base_exp", 5.0)));
        Assert.Equal(30, rates.Current.AwardExp(10));
    }

    [Fact]
    public void DropChanceScalesAndIsCappedAtCertainty()
    {
        var rates = new ServerRates();
        rates.Apply(Rows(("drop", 2.0)));
        Assert.Equal(800_000, rates.Current.DropChance(400_000));
        Assert.Equal(1_000_000, rates.Current.DropChance(600_000));
        rates.Apply(Rows(("drop", 0.5)));
        Assert.Equal(500, rates.Current.DropChance(1_000));
    }

    [Fact]
    public void ZenyScalesOnlyTheZenyEntriesRolledAfterTheStart()
    {
        var rates = new ServerRates();
        rates.Apply(Rows(("zeny", 2.5)));
        var drops = new List<(int, int)> { (1, 10), (1, 10), (1000000, 3), (1, 1) };
        rates.Current.ScaleZeny(drops, 1);
        Assert.Equal([(1, 10), (1, 25), (1000000, 3), (1, 2)], drops);
    }

    [Fact]
    public void RatesAreClampedAndUnknownOrMissingNamesFallBackToOne()
    {
        var rates = new ServerRates();
        rates.Apply(Rows(("base_exp", 50), ("job_exp", 0.01), ("drop", double.NaN), ("bogus", 4)));
        Assert.Equal(new ServerRates.Values(10.0, 0.1, 1.0, 1.0, 1.0), rates.Current);
        rates.Apply(Rows());
        Assert.Equal(ServerRates.Values.Default, rates.Current);
    }

    private sealed class FakeSource : IServerRatesSource
    {
        public Func<IReadOnlyDictionary<string, double>> Next { get; set; } = () => new Dictionary<string, double>();
        public IReadOnlyDictionary<string, double> Read() => Next();
    }

    private sealed class CountingLogger : ILogger<ServerRatesReader>
    {
        public int Warnings;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }

    [Fact]
    public void ReaderKeepsTheLastGoodValuesOnErrorAndWarnsOnce()
    {
        var rates = new ServerRates();
        var source = new FakeSource { Next = () => Rows(("drop", 3.0)) };
        var log = new CountingLogger();
        var reader = new ServerRatesReader(rates, source, log, TimeProvider.System);
        Assert.True(reader.Refresh());
        Assert.Equal(3.0, rates.Current.Drop);

        source.Next = () => throw new SqliteException("database is locked", 5);
        Assert.False(reader.Refresh());
        Assert.False(reader.Refresh());
        Assert.Equal(3.0, rates.Current.Drop);
        Assert.Equal(1, log.Warnings);

        source.Next = () => Rows();
        Assert.True(reader.Refresh());
        Assert.Equal(ServerRates.Values.Default, rates.Current);
    }

    [Fact]
    public void SqliteSourceReadsTheRealmTableAndToleratesItsAbsence()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ro3emu-rates-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = dir })))
            {
                var source = new SqliteServerRatesSource(accounts);
                Assert.Empty(source.Read()); // no konsor_rate table: every rate stays 1.0
                using (var db = new SqliteConnection($"Data Source={Path.Combine(dir, "ro3emu.db")};Pooling=False"))
                {
                    db.Open();
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE IF NOT EXISTS konsor_rate (name TEXT PRIMARY KEY, value REAL NOT NULL, updated_utc TEXT NOT NULL);
                        INSERT INTO konsor_rate VALUES ('base_exp', 2.5, '2026-10-30T00:00:00Z'), ('zeny', 'abc', '2026-10-30T00:00:00Z');
                        """;
                    cmd.ExecuteNonQuery();
                }
                var read = source.Read();
                Assert.Equal(2.5, read["base_exp"]);
                Assert.False(read.ContainsKey("zeny")); // a non-numeric value is ignored
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task HostAppliesTheQuestRateToAwardExpAndSharesOneRatesInstance()
    {
        await using var host = await SyntheticHost.StartAsync();
        var rates = host.Service<ServerRates>();
        Assert.Same(rates, host.Service<CharacterService>().Rates);
        Assert.Same(rates, host.Service<RewardService>().Rates);
        await using var bot = await host.InWorldAsync("RateAlice");
        var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Name == "RateAlice");
        var loop = host.Service<GameLoop>();
        var character = host.Service<CharacterService>();
        // Synthetic BaseLvConfig: level 1 needs 6 Base EXP, so both awards stay below a level-up.
        var plan = new AwardPlan(0, 0, [new AwardGrant(100, 1), new AwardGrant(101, 5)]);

        Assert.True(await loop.InvokeAsync(() => character.ApplyAwardAsync(session, plan)));
        Assert.Equal((1L, 5L), (session.Progress!.Exp, session.Progress.JobExp)); // 1.0: unchanged

        rates.Apply(Rows(("quest_exp", 2.0), ("base_exp", 9.0)));
        Assert.True(await loop.InvokeAsync(() => character.ApplyAwardAsync(session, plan)));
        Assert.Equal((3L, 15L), (session.Progress!.Exp, session.Progress.JobExp));
    }
}
