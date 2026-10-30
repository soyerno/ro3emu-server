using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The Konsor activity factors (not original behavior): refine success, craft and egg quality, card / equipment / MVP
/// drops, life-job and pass EXP. The synthetic data set has no refine, craft, incubation or drop tables, so these test
/// the rate helpers each service calls; the drop-group classification runs on invented tables in a temp directory.
/// </summary>
public sealed class ServerRatesFactorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-rate-factors-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static ServerRates.Values With(params (string Name, double Value)[] rows)
    {
        var rates = new ServerRates();
        rates.Apply(rows.ToDictionary(r => r.Name, r => r.Value));
        return rates.Current;
    }

    [Fact]
    public void TheNewFactorsAreParsedClampedAndDefaultToOne()
    {
        var rates = With(("refine_success", 2), ("craft_quality", 50), ("egg_quality", 0.01), ("card_drop", 3),
            ("equip_drop", 4), ("mvp_drop", 5), ("life_exp", 6), ("pass_exp", double.PositiveInfinity));
        Assert.Equal((2.0, 10.0, 0.1, 3.0, 4.0, 5.0, 6.0, 1.0), (rates.RefineSuccess, rates.CraftQuality, rates.EggQuality,
            rates.CardDrop, rates.EquipDrop, rates.MvpDrop, rates.LifeExp, rates.PassExp));
        Assert.Equal(ServerRates.Values.Default, With());
    }

    [Fact]
    public void DefaultFactorsLeaveEveryNewRewardUnchanged()
    {
        var rates = ServerRates.Values.Default;
        Assert.Equal(12_345, rates.RefineChance(4_000, 345, 8_000));
        Assert.Equal(0, rates.RefineChance(0, 0, 0));
        Assert.Equal(7, rates.CraftWeight(7));
        Assert.Equal(0, rates.EggWeight(0));
        Assert.Equal(333_333.5, rates.DropChance(333_333.5, ServerRates.DropKind.Card));
        Assert.Equal(2_000_000, rates.DropChance(2_000_000, ServerRates.DropKind.Equipment));
        Assert.Equal(rates, rates.ForAward());
        Assert.Equal((0L, 17L, 17L), (rates.LifeJobExp(0), rates.LifeJobExp(17), rates.PassPlayExp(17)));
    }

    [Fact]
    public void RefineScalesOnlyTheTableChanceAndIsCappedAtCertainty()
    {
        var rates = With(("refine_success", 2));
        Assert.Equal(5_000 + 300 + 0, rates.RefineChance(2_500, 300, 0)); // luck and Legend Ore are not scaled
        Assert.Equal(ServerRates.RefineCertain, rates.RefineChance(6_000, 0, 0));
        Assert.Equal(ServerRates.RefineCertain, rates.RefineChance(4_000, 1_000, 2_000));
        Assert.Equal(0, rates.RefineChance(0, 0, 0)); // a level that cannot succeed still cannot
    }

    [Fact]
    public void CraftAndEggWeightsScaleWithTheirOwnRateAndKeepZeroWeightsAtZero()
    {
        var rates = With(("craft_quality", 3), ("egg_quality", 0.5));
        Assert.Equal((30L, 0L), (rates.CraftWeight(10), rates.CraftWeight(0)));
        Assert.Equal((5L, 1L, 0L), (rates.EggWeight(10), rates.EggWeight(1), rates.EggWeight(0)));
    }

    [Fact]
    public void OnlyOrangeOrBetterCraftQualitiesAreBiased()
    {
        var rates = With(("craft_quality", 4));
        Assert.Equal(10, EquipmentService.QualityWeight(rates, EquipmentService.QualityOrange - 1, 10));
        Assert.Equal(40, EquipmentService.QualityWeight(rates, EquipmentService.QualityOrange, 10));
        Assert.Equal(40, EquipmentService.QualityWeight(rates, EquipmentService.QualityRed, 10));
        Assert.Equal(0, EquipmentService.QualityWeight(rates, EquipmentService.QualityRed, 0));
    }

    [Fact]
    public void OnlyGuaranteedBannerEggOutcomesAreBiased()
    {
        var rates = With(("egg_quality", 2));
        Assert.Equal(20, PetService.OutcomeWeight(rates, sClass: true, 10));
        Assert.Equal(10, PetService.OutcomeWeight(rates, sClass: false, 10));
    }

    [Fact]
    public void CardAndEquipmentFactorsStackOnTheGeneralDropFactorOnlyForPureGroups()
    {
        var rates = With(("drop", 2), ("card_drop", 3), ("equip_drop", 0.5), ("mvp_drop", 4));
        Assert.Equal(200, rates.DropChance(100, ServerRates.DropKind.Mixed));
        Assert.Equal(600, rates.DropChance(100, ServerRates.DropKind.Card));
        Assert.Equal(100, rates.DropChance(100, ServerRates.DropKind.Equipment));
        Assert.Equal(1_000_000, rates.DropChance(400_000, ServerRates.DropKind.Card));
        // RollAward (MVP and Monster Hunt) replaces the general drop factor by mvp_drop; card / equip still apply.
        var award = rates.ForAward();
        Assert.Equal(400, award.DropChance(100, ServerRates.DropKind.Mixed));
        Assert.Equal(1_200, award.DropChance(100, ServerRates.DropKind.Card));
        Assert.Equal(rates.Zeny, award.Zeny);
    }

    [Fact]
    public void LifeAndPassExpScaleWithTheirOwnRates()
    {
        var rates = With(("life_exp", 2.5), ("pass_exp", 0.1), ("quest_exp", 9));
        Assert.Equal((25L, 0L), (rates.LifeJobExp(10), rates.LifeJobExp(0)));
        Assert.Equal((30L, 1L), (rates.PassPlayExp(300), rates.PassPlayExp(6)));
    }

    private void Write(string table, object rows)
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", table + ".json"), JsonSerializer.Serialize(rows));
    }

    [Fact]
    public void ADropGroupIsCardOrEquipmentOnlyWhenEveryEligibleRowIs()
    {
        // Invented items: 11/12 cards (type 4 / subtype 400), 21/22 equipment (type 5), 31 a material.
        Write("ItemConfig", new Dictionary<string, object>
        {
            ["11"] = new { _iID = 11, _iType = 4, _iSubType = 400 }, ["12"] = new { _iID = 12, _iType = 4, _iSubType = 400 },
            ["21"] = new { _iID = 21, _iType = 5 }, ["22"] = new { _iID = 22, _iType = 5 },
            ["31"] = new { _iID = 31, _iType = 2 }, ["41"] = new { _iID = 41, _iType = 4, _iSubType = 401 },
        });
        static object Row(int item, int weight = 10, int quest = 0) => new { _kDropItem = item, _iWeights = weight, _iQuestLimit = quest };
        Write("DropGroupConfig", new Dictionary<string, object[]>
        {
            ["1"] = [Row(11), Row(12)],
            ["2"] = [Row(21), Row(22), Row(31, weight: 0)], // a zero-weight row is never drawn
            ["3"] = [Row(11), Row(21)],
            ["4"] = [Row(21), Row(31, quest: 5)], // a quest-limited row is never drawn
            ["5"] = [Row(41)],
        });
        var data = new GameData(Options.Create(new ServerOptions { GameDataDir = _root }), NullLogger<GameData>.Instance);
        Assert.Equal(ServerRates.DropKind.Card, RewardService.ClassifyGroup(data, 1));
        Assert.Equal(ServerRates.DropKind.Equipment, RewardService.ClassifyGroup(data, 2));
        Assert.Equal(ServerRates.DropKind.Mixed, RewardService.ClassifyGroup(data, 3));
        Assert.Equal(ServerRates.DropKind.Equipment, RewardService.ClassifyGroup(data, 4));
        Assert.Equal(ServerRates.DropKind.Mixed, RewardService.ClassifyGroup(data, 5)); // type 4 but not a card subtype
        Assert.Equal(ServerRates.DropKind.Mixed, RewardService.ClassifyGroup(data, 99)); // no such group
    }

    [Fact]
    public async Task HostSharesTheRatesInstanceWithTheNewFactorServices()
    {
        await using var host = await SyntheticHost.StartAsync();
        var rates = host.Service<ServerRates>();
        Assert.Same(rates, host.Service<EquipmentService>().Rates);
        Assert.Same(rates, host.Service<PetService>().Rates);
        Assert.Same(rates, host.Service<LifeJobService>().Rates);
        Assert.Same(rates, host.Service<NewActivityService>().Rates);
    }
}
