using Ro3Emu.DataExport;
using Ro3Emu.Loc;

namespace Ro3Emu.Setup.Tests;

/// <summary>
/// Announcements (SystemBoardCastMsgNtf) reach the screen as a finished English sentence: the chat code fills the
/// Localization_en text of a ChatConfig content id (${n} the server's params, ^{n} the ChatConfig colour tags) without
/// going through the hooked lookup by id. loc_sid_es.tsv therefore also carries "en\tEnglish\tSpanish" lines, and
/// <see cref="LocTemplates"/> turns the finished English into the Spanish sentence.
/// </summary>
public sealed class LocSentenceTests : IDisposable
{
    // Localization 10320000545 / 10320000546 (ChatConfig 9001 / 9002, the Rift announcements) as the client has them.
    private const string RiftZh = "裂隙之石在^{1}${1}^{2}的^{3}${2}^{4}线刷新，冒险者们快去挑战吧~";
    private const string RiftEn = "A Rift Stone has appeared on Channel ^{3}${2}^{4} in ^{1}${1}^{2}. Adventurers, hurry over and challenge it~";
    private const string RiftEs = "Ha aparecido una Piedra de grieta en el Canal ^{3}${2}^{4} de ^{1}${1}^{2}. ¡Aventureros, apresúrense a desafiarla~!";
    private const string EndZh = "恭喜各位冒险者成功平定了本次裂隙危机。";
    private const string EndEn = "Congratulations to all Adventurers for successfully resolving this Rift Crisis.";
    private const string EndEs = "Felicidades a todos los aventureros por resolver con éxito esta Crisis de grietas.";

    // ChatConfig 9001 _iContent: ^{1}..^{4} are these tags; ${1} the map, ${2} the channel.
    private const string RiftShown = "A Rift Stone has appeared on Channel <color=#CC762A>3</color> in <color=#CC762A>Prontera</color>. Adventurers, hurry over and challenge it~";
    private const string RiftShownEs = "Ha aparecido una Piedra de grieta en el Canal <color=#CC762A>3</color> de <color=#CC762A>Prontera</color>. ¡Aventureros, apresúrense a desafiarla~!";

    private readonly string _dir = Directory.CreateTempSubdirectory("ro3emu-locsentence-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string Json(params (long Id, string Text)[] rows) =>
        System.Text.Json.JsonSerializer.Serialize(rows.ToDictionary(r => r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), r => new Dictionary<string, string> { ["_kDes"] = r.Text }));

    private List<(string En, string Es)> Sentences(string zhJson, string enJson, params string[] translations)
    {
        var zh = Path.Combine(_dir, "zh.json");
        var en = Path.Combine(_dir, "en.json");
        File.WriteAllText(zh, zhJson);
        File.WriteAllText(en, enJson);
        var map = new LocMapBuilder();
        map.AddClientTables(zh, en);
        map.AddTranslations(translations);
        return map.SentenceEntries(zh, en).ToList();
    }

    [Fact]
    public void TheRiftAnnouncementsGiveTheirEnglishAndSpanishSentences()
    {
        var sentences = Sentences(Json((10320000545, RiftZh), (10320000546, EndZh)), Json((10320000545, RiftEn), (10320000546, EndEn)),
            RiftZh + "\t" + RiftEs + "\n" + EndZh + "\t" + EndEs + "\n");
        Assert.Equal([(RiftEn, RiftEs), (EndEn, EndEs)], sentences);
    }

    [Fact]
    public void ASpanishSentenceThatLosesAColourMarkerIsSkipped()
    {
        var sentences = Sentences(Json((1, RiftZh)), Json((1, RiftEn)), RiftZh + "\tHa aparecido una Piedra de grieta en ${2} de ${1}.\n");
        Assert.Empty(sentences);
    }

    [Fact]
    public void SentencesAreWrittenAfterTheIdsWhereAnIdReaderSkipsThem()
    {
        var file = Path.Combine(_dir, GameDataExport.SidMapFile);
        int count = LocMapBuilder.WriteSids([(10320000546L, EndEs)], file, [(RiftEn, RiftEs), ("Two\nlines", "Dos\nlíneas")]);
        var lines = File.ReadAllLines(file);
        Assert.Equal(1, count);
        Assert.StartsWith(LocMapBuilder.GeneratedSidHeader, lines[0]);
        Assert.Equal(["10320000546\t" + EndEs, "en\t" + RiftEn + "\t" + RiftEs, "en\tTwo\\nlines\tDos\\nlíneas"], lines[1..]);
    }

    [Fact]
    public void TheFilledRiftAnnouncementBecomesSpanishWithItsColoursAndParams()
    {
        var t = new LocTemplates();
        Assert.True(t.Add(RiftEn, RiftEs));
        Assert.Equal(RiftShownEs, t.Translate(RiftShown, null));
        Assert.Equal("  " + RiftShownEs + "\n", t.Translate("  " + RiftShown + "\n", null));
    }

    [Fact]
    public void AnAnnouncementWithoutColourTagsStillMatches()
    {
        var t = new LocTemplates();
        t.Add(RiftEn, RiftEs);
        Assert.Equal("Ha aparecido una Piedra de grieta en el Canal 2 de Geffen. ¡Aventureros, apresúrense a desafiarla~!",
            t.Translate("A Rift Stone has appeared on Channel 2 in Geffen. Adventurers, hurry over and challenge it~", null));
    }

    [Fact]
    public void ParamsGoThroughTheCaptureMap()
    {
        var t = new LocTemplates();
        t.Add(RiftEn, RiftEs);
        string? Capture(string s) => s == "Prontera" ? "Prontera (es)" : null;
        Assert.Equal(RiftShownEs.Replace("Prontera", "Prontera (es)", StringComparison.Ordinal), t.Translate(RiftShown, Capture));
    }

    [Fact]
    public void AnAnnouncementWithoutParamsIsAnExactSentence()
    {
        var t = new LocTemplates();
        Assert.True(t.Add(EndEn, EndEs));
        Assert.True(t.Add("【Gather】", "【Reunión】"));
        Assert.Equal(EndEs, t.Translate(EndEn, null));
        Assert.Equal("【Reunión】", t.Translate("【Gather】", null));
        Assert.Null(t.Translate("Congratulations to all Adventurers", null));
    }

    [Fact]
    public void TextOutsideEveryTemplateIsLeftAlone()
    {
        var t = new LocTemplates();
        t.Add(RiftEn, RiftEs);
        Assert.Null(t.Translate("A Rift Stone has appeared somewhere else", null));
        Assert.Null(t.Translate("Hello there", null));
        Assert.Null(t.Translate("", null));
    }

    [Fact]
    public void ATemplateWithoutAnAnchorWordOrWithAmbiguousSpanishIsNotUsed()
    {
        var t = new LocTemplates();
        Assert.False(t.Add("${1}", "${1}"));
        Assert.False(t.Add("${1}: ${2}", "${1}: ${2}"));
        Assert.False(t.Add("Gain ${1} zeny", "Obtienes zeny"));
        Assert.True(t.Add("Close", "Cerrar"));
        Assert.False(t.Add("Close", "Cercano"));
        Assert.Null(t.Translate("Close", null));
        Assert.True(t.Add("${1} is online!", "¡${1} está en línea!"));
        Assert.False(t.Add("${1} is online!", "${1} se conectó"));
        Assert.Null(t.Translate("Poring is online!", null));
    }

    [Fact]
    public void GatherHasItsSpanishInOurTranslations()
    {
        var zh = Path.Combine(_dir, "zh.json");
        var en = Path.Combine(_dir, "en.json");
        File.WriteAllText(zh, Json((10320000037, "【集合】")));
        File.WriteAllText(en, Json((10320000037, "【Gather】")));
        var map = new LocMapBuilder();
        map.AddClientTables(zh, en);
        map.AddTranslations(Flows.Translations());
        Assert.Equal([(10320000037L, "【Reunión】")], map.SidEntries(zh, en).ToList());
        Assert.Equal([("【Gather】", "【Reunión】")], map.SentenceEntries(zh, en).ToList());
    }
}
