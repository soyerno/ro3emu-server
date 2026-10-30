using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The client's "Redemption Code" screen (LC_RedemptionCodeManager.UseGiftCard): a JSON POST to GMTUrl() +
/// <see cref="UsePath"/> with the header GameCode and the body gift_card_code, game_code, platform_code, group_id,
/// open_id, role_id, language, big_area, account_id, account_token. The client reads a JSON {code, message} answer:
/// 1959 or 1960 is success, anything else a refusal whose message it shows. Every answer here, a rate-limited one
/// included, is that JSON with HTTP 200. See docs/systems/gift-codes.md.
/// </summary>
public static class GiftCodeEndpoints
{
    public const string UsePath = "/gift-card/use/v2";

    /// <summary>Konsor addition (not original): the website backend's redemption, see <see cref="MapSite"/>.</summary>
    public const string SiteRedeemPath = "/ro3emu/account/redeem-gift";

    /// <summary>The header that carries <see cref="ServerOptions.SiteApiKey"/>.</summary>
    public const string SiteKeyHeader = "X-Ro3Emu-Site-Key";

    /// <summary>
    /// Konsor addition (not original; the client has no such request). The Konsor website's backend, on the same
    /// machine, redeems a code for its signed-in account: POST form account (login name), role_id, code, with
    /// <see cref="SiteKeyHeader"/> equal to Ro3Emu:SiteApiKey. Loopback only and off while the key is empty (404); a
    /// wrong key is 403, a missing or invalid field 400. Otherwise HTTP 200 with the same JSON {code, message} and the
    /// same refusals as <see cref="UsePath"/> (GiftCodeService.RedeemForAccountAsync).
    /// </summary>
    public static void MapSite(WebApplication app) =>
        app.MapPost(SiteRedeemPath, async (HttpRequest request, IOptions<ServerOptions> options, GiftCodeService gifts) =>
        {
            if (request.HttpContext.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)
                || options.Value.SiteApiKey is not { Length: > 0 } key)
                return Results.NotFound();
            if (!KeyMatches(request.Headers[SiteKeyHeader].ToString(), key)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (await ReadSiteFormAsync(request) is not { } form)
                return Results.Json(Body(GiftCodeService.Answer(ErrorCodes.GiftCardBadRequest)), statusCode: StatusCodes.Status400BadRequest);
            return Reply(await gifts.RedeemForAccountAsync(form.Account, form.RoleId, form.Code));
        });

    /// <summary>Constant-time comparison of the sent key with the configured one (hashed first, so not even the length leaks).</summary>
    private static bool KeyMatches(string sent, string key) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(sent)), SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private sealed record SiteForm(string Account, string RoleId, string Code);

    /// <summary>The three form fields, or null when the body is not a form or a field is missing or invalid.</summary>
    private static async Task<SiteForm?> ReadSiteFormAsync(HttpRequest request)
    {
        if (!request.HasFormContentType || request.ContentLength > MaxBodyBytes) return null;
        var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
        string account = form["account"].ToString().Trim(), roleId = form["role_id"].ToString().Trim(),
            code = form["code"].ToString().Trim();
        bool validRole = long.TryParse(roleId, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0;
        return account.Length > 0 && validRole && code.Length > 0 ? new SiteForm(account, roleId, code) : null;
    }

    /// <summary>(inferred) The largest body read; the client's is a few hundred bytes.</summary>
    public const int MaxBodyBytes = 8192;

    public static void Map(WebApplication app) =>
        app.MapPost(UsePath, async (HttpRequest request, GiftCodeLimiter limiter, GiftCodeService gifts) =>
        {
            // (inferred) The body is read whatever Content-Type says (or if it is missing): the client's HTTP layer
            // sets application/json, but nothing else here depends on it. game_code and the GameCode header are not
            // checked: this server hosts one game.
            var body = await ReadAsync(request);
            if (!limiter.TryAcquire(request.HttpContext.Connection.RemoteIpAddress, null))
                return Reply(GiftCodeService.Answer(ErrorCodes.GiftCardRateLimited));
            if (body == null) return Reply(GiftCodeService.Answer(ErrorCodes.GiftCardBadRequest));
            return Reply(await gifts.RedeemAsync(body));
        });

    private static IResult Reply(GiftCodeAnswer answer) => Results.Json(Body(answer));

    private static object Body(GiftCodeAnswer answer) => new { code = answer.Code, message = answer.Message };

    /// <summary>The fields the server uses, or null for a body that is too large or not a JSON object.</summary>
    private static async Task<GiftCodeRequest?> ReadAsync(HttpRequest request)
    {
        if (request.ContentLength > MaxBodyBytes) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        try
        {
            using var json = JsonDocument.Parse(buffer.ToArray());
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return new GiftCodeRequest(Text(root, "account_id"), Text(root, "account_token"), Text(root, "role_id"),
                Text(root, "gift_card_code"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A string field, or a number's literal text (role_id may come either way); null otherwise.</summary>
    private static string? Text(JsonElement root, string name) =>
        !root.TryGetProperty(name, out var value) ? null : value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
}
