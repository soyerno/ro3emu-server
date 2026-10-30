using System.Globalization;
using System.Net;
using System.Text.Json;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The loopback quest debugging endpoints (docs/guide/gm-access.md): GET /ro3emu/quests (catalog), GET
/// /ro3emu/quests?account= (an online role's quests) and the reply text in the body of POST /ro3emu/gm. The synthetic
/// fixture gets three invented TaskConfig rows and their invented names.
/// </summary>
public sealed class QuestDebugTests : IAsyncLifetime
{
    private const long Alpha = 900001, Beta = 900002, Gamma = 900003;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-questdebug-" + Guid.NewGuid().ToString("N"));
    private SyntheticHost _host = null!;

    public async Task InitializeAsync()
    {
        foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(_root, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        // Rows out of id order, so the catalog has to sort them. Alpha's one target waits for Gamma's delivery. The story
        // root (TaskService.FirstTask) turns the quest system on for new roles.
        Write("TaskConfig", new Dictionary<string, object>
        {
            [Key(TaskService.FirstTask)] = Quest(TaskService.FirstTask, 91000, []),
            [Key(Gamma)] = Quest(Gamma, 91003, []),
            [Key(Alpha)] = Quest(Alpha, 91001, [[104004, Gamma]]),
            [Key(Beta)] = Quest(Beta, 91002, []),
        });
        Write("Localization_en", new Dictionary<string, object>
        {
            ["91000"] = new { _iID = 91000, _kDes = "First Errand" },
            ["91001"] = new { _iID = 91001, _kDes = "Alpha Errand" },
            ["91002"] = new { _iID = 91002, _kDes = "Beta Errand" },
            ["91003"] = new { _iID = 91003, _kDes = "Gamma Errand" },
        });
        _host = await SyntheticHost.StartAsync(o => o.GameDataDir = _root);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task CatalogListsEveryQuestSortedById()
    {
        using var client = new HttpClient();
        var resp = await client.GetAsync(_host.Http + "/ro3emu/quests");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var rows = doc.RootElement.EnumerateArray().Select(e => (e.GetProperty("id").GetInt64(), e.GetProperty("name").GetString())).ToList();
        Assert.Equal([(Alpha, "Alpha Errand"), (Beta, "Beta Errand"), (Gamma, "Gamma Errand"), (TaskService.FirstTask, "First Errand")], rows);
    }

    [Fact]
    public async Task StateShowsAcceptedQuestAndCompletedCount()
    {
        var bot = await _host.InWorldAsync("QuestDebugAda");
        Assert.StartsWith($"accept {Alpha}", await Gm(bot.Account!, $"!quest accept {Alpha}"));
        var session = _host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        await _host.Service<GameLoop>().InvokeAsync(() =>
        {
            session.Tasks!.CompleteTaskNum[Beta] = 2;
            return Task.CompletedTask;
        });

        using var client = new HttpClient();
        var resp = await client.GetAsync($"{_host.Http}/ro3emu/quests?account={bot.Account!.ToUpperInvariant()}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(bot.Account, root.GetProperty("account").GetString());
        Assert.Equal("QuestDebugAda", root.GetProperty("role").GetString());
        var quests = root.GetProperty("quests").EnumerateArray().ToDictionary(q => q.GetProperty("id").GetInt64());
        var alpha = quests[Alpha];
        Assert.Equal("Alpha Errand", alpha.GetProperty("name").GetString());
        Assert.Equal("running", alpha.GetProperty("status").GetString());
        var progress = Assert.Single(alpha.GetProperty("progress").EnumerateArray());
        Assert.Equal(0, progress.GetProperty("done").GetInt64());
        Assert.Equal(1, progress.GetProperty("need").GetInt64());
        Assert.Equal(0, alpha.GetProperty("completed").GetInt32());
        Assert.Equal(2, quests[Beta].GetProperty("completed").GetInt32());
        Assert.Equal("delivery", quests[Beta].GetProperty("status").GetString());
    }

    [Fact]
    public async Task GmReturnsTheCommandReply()
    {
        var bot = await _host.InWorldAsync("QuestDebugBo");
        await Gm(bot.Account!, $"!quest accept {Alpha}");
        Assert.StartsWith($"drop {Alpha}", await Gm(bot.Account!, $"quest drop {Alpha}"));
        Assert.StartsWith("Unknown command.", await Gm(bot.Account!, "!nosuchcommand"));
    }

    [Fact]
    public async Task StateOfAnAccountNotInGameIsRefused()
    {
        using var client = new HttpClient();
        var resp = await client.GetAsync(_host.Http + "/ro3emu/quests?account=nobody");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("not in game", await resp.Content.ReadAsStringAsync());
    }

    /// <summary>Runs a GM line through the loopback tool and returns the reply text of its 200 body.</summary>
    private async Task<string> Gm(string account, string line)
    {
        using var client = new HttpClient();
        var resp = await client.PostAsync($"{_host.Http}/ro3emu/gm?account={account}", new StringContent(line));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonSerializer.Deserialize<string>(await resp.Content.ReadAsStringAsync()) ?? "";
    }

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static object Quest(long id, long nameText, long[][] targets) => new
    {
        _iTaskID = id, _iTaskType = 1, _iName = new[] { nameText }, _kAcceptTask = new[] { 3 }, _kDeliverTask = new[] { 3 },
        _kProgressTarget = targets,
    };

    private void Write(string name, object value) =>
        File.WriteAllText(Path.Combine(_root, "config", name + ".json"), JsonSerializer.Serialize(value));
}
