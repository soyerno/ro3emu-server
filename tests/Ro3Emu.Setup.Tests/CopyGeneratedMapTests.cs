using Ro3Emu.DataExport;

namespace Ro3Emu.Setup.Tests;

/// <summary>
/// <see cref="Flows.CopyGeneratedMap"/>: the text maps in data/ go next to the game (&lt;client&gt;/ro3emu/), replacing a
/// map we generated earlier but never a hand-made one.
/// </summary>
public sealed class CopyGeneratedMapTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-copymap-" + Guid.NewGuid().ToString("N"));
    string Data => Path.Combine(_root, "data");
    string Client => Path.Combine(_root, "client");
    string Target => Path.Combine(Client, "ro3emu", GameDataExport.SidMapFile);

    public CopyGeneratedMapTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Client);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    void Copy() => Flows.CopyGeneratedMap(Data, Client, GameDataExport.SidMapFile, LocMapBuilder.GeneratedSidHeader);

    static string Generated(string body) => LocMapBuilder.GeneratedSidHeader + " test\n" + body;

    [Fact]
    public void CopiesTheSidMapNextToTheGame()
    {
        File.WriteAllText(Path.Combine(Data, GameDataExport.SidMapFile), Generated("1\tHola\n"));

        Copy();

        Assert.Equal(Generated("1\tHola\n"), File.ReadAllText(Target));
    }

    [Fact]
    public void ReplacesAMapWeGeneratedBefore()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, Generated("1\tViejo\n"));
        File.WriteAllText(Path.Combine(Data, GameDataExport.SidMapFile), Generated("1\tNuevo\n"));

        Copy();

        Assert.Equal(Generated("1\tNuevo\n"), File.ReadAllText(Target));
    }

    [Fact]
    public void KeepsAHandMadeMap()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, "1\tMine\n");
        File.WriteAllText(Path.Combine(Data, GameDataExport.SidMapFile), Generated("1\tNuevo\n"));

        Copy();

        Assert.Equal("1\tMine\n", File.ReadAllText(Target));
    }

    [Fact]
    public void DoesNothingWithoutAMapInData()
    {
        Copy();

        Assert.False(File.Exists(Target));
    }
}
