using System.Globalization;
using System.Net;
using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Http;

namespace Ro3Emu.Tests;

/// <summary>
/// The website's gift code redemption (Konsor addition, docs/systems/gift-codes.md): the site's backend posts the
/// signed-in account, one of its roles and the code to POST /ro3emu/account/redeem-gift on loopback with the shared
/// site key, and the server redeems it through the same rules and the same mail as the client's screen.
/// </summary>
public sealed class SiteGiftCodeTests(SiteGiftCodeTests.HostFixture fixture) : IClassFixture<SiteGiftCodeTests.HostFixture>
{
    private const string SiteKey = "synthetic-site-key";
    private const int Potion = 1000000;
    private readonly SyntheticHost _host = fixture.Host;

    public sealed class HostFixture : IAsyncLifetime
    {
        public SyntheticHost Host { get; private set; } = null!;
        public async Task InitializeAsync() => Host = await SyntheticHost.StartAsync(o => o.SiteApiKey = SiteKey);
        public async Task DisposeAsync() => await Host.DisposeAsync();
    }

    private sealed record Player(string Account, long Role);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static async Task<Player> PlayerAsync(SyntheticHost host)
    {
        string account = Unique("site");
        Assert.Equal(AccountStore.Registration.Created, await host.Service<AccountStore>().RegisterAsync(account, "pw-" + account));
        return new(account, host.Service<RoleStore>().Create(account, 1, Unique("S"), 1, 1000, 1)!.Id);
    }

    private static Dictionary<string, string> Form(string account, long role, string code) => new()
    {
        ["account"] = account, ["role_id"] = role.ToString(CultureInfo.InvariantCulture), ["code"] = code,
    };

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(SyntheticHost host,
        Dictionary<string, string> form, string? key = SiteKey)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, host.Http + GiftCodeEndpoints.SiteRedeemPath)
        {
            Content = new FormUrlEncodedContent(form),
        };
        if (key != null) request.Headers.Add(GiftCodeEndpoints.SiteKeyHeader, key);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<int> RedeemAsync(string account, long role, string code)
    {
        var (status, body) = await PostAsync(_host, Form(account, role, code));
        Assert.Equal(HttpStatusCode.OK, status);
        using var json = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("message").GetString()));
        return json.RootElement.GetProperty("code").GetInt32();
    }

    private int Letters(long role) =>
        JsonSerializer.Deserialize<MailService.Mailbox>(_host.Service<RoleStore>().LoadMailbox(role) ?? "{}"u8.ToArray())!
            .Letters.Count(l => l.Title == GiftCodeService.MailTitle);

    private long Redemptions(string code) =>
        GiftCodeTests.Scalar(_host, "SELECT COUNT(*) FROM gift_code_redemption WHERE code=$c", ("$c", code));

    [Fact]
    public async Task AValidCodeForOwnRoleMailsOnceAndASecondAttemptIsRefused()
    {
        string code = Unique("WS");
        GiftCodeTests.Panel(_host, code, $"[[{Potion},2]]");
        var player = await PlayerAsync(_host);

        Assert.Equal(ErrorCodes.GiftCardUseSuccess, await RedeemAsync(player.Account, player.Role, code));
        Assert.Equal(ErrorCodes.GiftCardAlreadyRedeemed, await RedeemAsync(player.Account, player.Role, code));

        Assert.Equal(1, Letters(player.Role));
        Assert.Equal(1, Redemptions(code));
        Assert.Equal(player.Role, GiftCodeTests.Scalar(_host, "SELECT role_id FROM gift_code_redemption WHERE code=$c", ("$c", code)));
    }

    [Fact]
    public async Task TheAccountIsMatchedInAnyLetterCaseAndRecordedByItsStoredName()
    {
        string code = Unique("WC");
        GiftCodeTests.Panel(_host, code, $"[[{Potion},1]]");
        var player = await PlayerAsync(_host);

        Assert.Equal(ErrorCodes.GiftCardUseSuccess, await RedeemAsync(player.Account.ToUpperInvariant(), player.Role, code));

        Assert.Equal(1, GiftCodeTests.Scalar(_host, "SELECT COUNT(*) FROM gift_code_redemption WHERE code=$c AND account=$a",
            ("$c", code), ("$a", player.Account)));
    }

    [Fact]
    public async Task AnotherAccountsRoleIsRefusedAndNothingIsWritten()
    {
        string code = Unique("WO");
        GiftCodeTests.Panel(_host, code, $"[[{Potion},1]]");
        var player = await PlayerAsync(_host);
        var other = await PlayerAsync(_host);

        Assert.Equal(ErrorCodes.GiftCardRoleNotOwned, await RedeemAsync(player.Account, other.Role, code));

        Assert.Equal(0, Redemptions(code));
        Assert.Null(_host.Service<RoleStore>().LoadMailbox(other.Role));
    }

    [Fact]
    public async Task AnUnknownCodeOrAnUnknownAccountIsRefused()
    {
        var player = await PlayerAsync(_host);

        Assert.Equal(ErrorCodes.GiftCardUnknown, await RedeemAsync(player.Account, player.Role, Unique("WU")));
        string code = Unique("WA");
        GiftCodeTests.Panel(_host, code, $"[[{Potion},1]]");
        Assert.Equal(ErrorCodes.GiftCardBadCredentials, await RedeemAsync(Unique("nobody"), player.Role, code));

        Assert.Equal(0, Redemptions(code));
        Assert.Null(_host.Service<RoleStore>().LoadMailbox(player.Role));
    }

    [Fact]
    public async Task AWrongOrMissingSiteKeyIsForbidden()
    {
        string code = Unique("WK");
        GiftCodeTests.Panel(_host, code, $"[[{Potion},1]]");
        var player = await PlayerAsync(_host);

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(_host, Form(player.Account, player.Role, code), "wrong")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(_host, Form(player.Account, player.Role, code), null)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(_host, Form(player.Account, player.Role, code), SiteKey + "x")).Status);

        Assert.Equal(0, Redemptions(code));
    }

    [Fact]
    public async Task MissingOrInvalidFieldsAreABadRequest()
    {
        var player = await PlayerAsync(_host);
        var bad = new Dictionary<string, string>[]
        {
            new() { ["role_id"] = player.Role.ToString(CultureInfo.InvariantCulture), ["code"] = "X" },
            new() { ["account"] = player.Account, ["code"] = "X" },
            new() { ["account"] = player.Account, ["role_id"] = "abc", ["code"] = "X" },
            new() { ["account"] = player.Account, ["role_id"] = "-5", ["code"] = "X" },
            new() { ["account"] = player.Account, ["role_id"] = player.Role.ToString(CultureInfo.InvariantCulture), ["code"] = "  " },
        };

        foreach (var form in bad)
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(_host, form)).Status);
    }

    [Fact]
    public async Task TheEndpointIsDisabledWithoutASiteKey()
    {
        await using var host = await SyntheticHost.StartAsync();
        var player = await PlayerAsync(host);
        string code = Unique("WD");
        GiftCodeTests.Panel(host, code, $"[[{Potion},1]]");

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(host, Form(player.Account, player.Role, code))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(host, Form(player.Account, player.Role, code), "")).Status);

        Assert.Equal(0, GiftCodeTests.Scalar(host, "SELECT COUNT(*) FROM gift_code_redemption WHERE code=$c", ("$c", code)));
    }

    [Fact]
    public async Task ThePerAccountRateLimitApplies()
    {
        await using var host = await SyntheticHost.StartAsync(o =>
        {
            o.SiteApiKey = SiteKey;
            o.GiftCodeAttemptsPerMinute = 2;
        });
        var player = await PlayerAsync(host);
        var codes = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            var answer = await PostAsync(host, Form(player.Account, player.Role, Unique("WR")));
            using var json = JsonDocument.Parse(answer.Body);
            codes.Add(json.RootElement.GetProperty("code").GetInt32());
        }

        Assert.Equal([ErrorCodes.GiftCardUnknown, ErrorCodes.GiftCardUnknown, ErrorCodes.GiftCardRateLimited], codes);
    }
}
