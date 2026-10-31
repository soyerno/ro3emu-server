using Ro3Emu.DataExport;

namespace Ro3Emu.Setup.Tests;

/// <summary>
/// The client looks skill names and most other text up by id in Localization_en, so its English never reaches the
/// Chinese map and our translations of that text never applied. loc_sid_es.tsv carries them by id, in Spanish: Konsor's
/// translations end with zz-es-full.tsv (zh -> es), and Ro3Loc reads the file only while language.txt selects Spanish.
/// </summary>
public sealed class LocSidMapTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ro3emu-locsid-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private List<(long Sid, string Text)> Sids(string zhJson, string enJson, string translations)
    {
        var zh = Path.Combine(_dir, "zh.json");
        var en = Path.Combine(_dir, "en.json");
        File.WriteAllText(zh, zhJson);
        File.WriteAllText(en, enJson);
        var map = new LocMapBuilder();
        map.AddClientTables(zh, en);
        map.AddTranslations([translations]);
        return map.SidEntries(zh, en).ToList();
    }

    [Fact]
    public void TheSidMapIsSpanishAndPartOfAnUpToDateExport()
    {
        Assert.Equal("loc_sid_es.tsv", GameDataExport.SidMapFile);
        Assert.Contains(GameDataExport.SidMapFile, GameDataExport.TopFiles);
    }

    [Fact]
    public void ALaterSpanishTranslationWinsForTheId()
    {
        var zh = Path.Combine(_dir, "zh.json");
        var en = Path.Combine(_dir, "en.json");
        File.WriteAllText(zh, """{"7":{"_kDes":"利刃重击"}}""");
        File.WriteAllText(en, """{"7":{"_kDes":"Blade Smash"}}""");
        var map = new LocMapBuilder();
        map.AddClientTables(zh, en);
        map.AddTranslations(["利刃重击\tSharp Blade Smash\n", "利刃重击\tGolpe de filo\n"]);
        Assert.Equal([(7L, "Golpe de filo")], map.SidEntries(zh, en).ToList());
    }

    [Fact]
    public void ATranslatedTextReplacesTheClientsEnglishForItsId()
    {
        var sids = Sids("""{"10110201064":{"_kDes":"利刃重击"}}""", """{"10110201064":{"_kDes":"Blade Smash"}}""", "利刃重击\tGolpe de filo\n");
        Assert.Equal([(10110201064L, "Golpe de filo")], sids);
    }

    [Fact]
    public void AnUntranslatedTextIsLeftToTheClient()
    {
        var sids = Sids("""{"1":{"_kDes":"取消"},"2":{"_kDes":"无英文"}}""", """{"1":{"_kDes":"Cancel"}}""", "");
        Assert.Empty(sids);
    }

    [Fact]
    public void ATranslationEqualToTheClientsEnglishIsSkipped()
    {
        var sids = Sids("""{"1":{"_kDes":"确定"}}""", """{"1":{"_kDes":"OK"}}""", "确定\tOK\n");
        Assert.Empty(sids);
    }

    [Fact]
    public void ATranslationThatLosesOrAddsAPlaceholderIsSkipped()
    {
        var sids = Sids(
            """{"1":{"_kDes":"造成{0}点伤害"},"2":{"_kDes":"获得${1}金币"},"3":{"_kDes":"剩余%d次"},"4":{"_kDes":"冷却{0}秒"}}""",
            """{"1":{"_kDes":"Deals {0} damage"},"2":{"_kDes":"Gain ${1} zeny"},"3":{"_kDes":"%d left"},"4":{"_kDes":"Cooldown {0}s"}}""",
            "造成{0}点伤害\tInflige daño\n获得${1}金币\tObtienes {1} zeny\n剩余%d次\tQuedan %s\n冷却{0}秒\tEnfriamiento {0} s\n");
        Assert.Equal([(4L, "Enfriamiento {0} s")], sids);
    }

    [Fact]
    public void ReorderedPlaceholdersAndAnIdWithoutEnglishAreKept()
    {
        var sids = Sids(
            """{"1":{"_kDes":"{0}对{1}造成伤害"},"2":{"_kDes":"等级@{1}"}}""",
            """{"1":{"_kDes":"{0} hits {1}"}}""",
            "{0}对{1}造成伤害\t{1} recibe daño de {0}\n等级@{1}\tNivel @{1}\n");
        Assert.Equal([(1L, "{1} recibe daño de {0}"), (2L, "Nivel @{1}")], sids);
    }

    [Fact]
    public void AChineseTargetAndANonNumericIdAreSkipped()
    {
        var sids = Sids("""{"1":{"_kDes":"坏"},"key":{"_kDes":"利刃重击"}}""", """{"1":{"_kDes":"Bad"},"key":{"_kDes":"Blade Smash"}}""",
            "坏\t坏的\n利刃重击\tGolpe de filo\n");
        Assert.Empty(sids);
    }

    [Theory]
    [InlineData("{0} de {1}", "{1} of {0}", true)]
    [InlineData("{0:00}/{1:00} MB", "{0:00} / {1:00} MB", true)]
    [InlineData("{0} {0}", "{0}", false)]
    [InlineData("${1}%", "${1} %", true)]
    [InlineData("@{1} % de", "@{1}% of", true)]
    [InlineData("%s %d", "%d %s", true)]
    [InlineData("%.1f", "%d", false)]
    [InlineData("{1}", "${1}", false)]
    public void PlaceholdersAreComparedAsAMultiset(string a, string b, bool same) => Assert.Equal(same, LocMapBuilder.SamePlaceholders(a, b));

    [Fact]
    public void TheSidMapIsWrittenWithItsGeneratedHeaderAndEscaped()
    {
        var file = Path.Combine(_dir, GameDataExport.SidMapFile);
        int count = LocMapBuilder.WriteSids([(1L, "Línea\nnueva\tcon \\ barra")], file);
        var lines = File.ReadAllLines(file);
        Assert.Equal(1, count);
        Assert.StartsWith(LocMapBuilder.GeneratedSidHeader, lines[0]);
        Assert.Equal(["1\tLínea\\nnueva\\tcon \\\\ barra"], lines[1..]);
    }
}
