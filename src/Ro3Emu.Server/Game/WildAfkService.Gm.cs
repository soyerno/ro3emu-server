using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Game;

/// <summary>
/// GM aids, not original behavior (the client has no such commands): !afkunlock keeps <see cref="Function"/> open for
/// one role (persisted in its wild_afk row), !afktime reads or changes the Bonus Time bank, !afkmode flips the switch
/// without the client's OnHookPauseButtonCD. Each change follows <see cref="ToggleAsync"/>'s order under the role scope
/// and the AFK gate: settle the clock, write the changed state, adopt it in memory, then resend 5035.
/// </summary>
public sealed partial class WildAfkService
{
    private const long SecondsPerMinute = 60;

    private void RegisterGm(GmCommands gm)
    {
        gm.Register("afkunlock", "on|off (GM aid: keep Wild AFK auto mode unlocked for this role, persisted)",
            (s, a) => GmChangeAsync(s, "unlock", $"GM unlock {OnOff(a)}", next => { next.GmUnlocked = On(a); return null; }));
        gm.Register("afktime", "info | set <minutes> | add <minutes> (GM aid: the Bonus Time bank)", AfkTimeAsync);
        gm.Register("afkmode", "on|off (GM aid: the Bonus Time switch, no client cooldown)",
            (s, a) => GmChangeAsync(s, "mode", $"Bonus Time switch {OnOff(a)}", next => SetMode(next, On(a))));
    }

    private static bool On(string[] args) => OnOff(args) == "ON";

    private static string OnOff(string[] args) => args.Length == 1 && args[0].ToLowerInvariant() is "on" or "off"
        ? args[0].ToUpperInvariant() : throw new ArgumentException("on|off");

    /// <summary>
    /// Minutes, clamped at zero. (GM aid) Not clamped to OnHookFightTimeLimit: the owner may grant more than the cap,
    /// and the daily restore's own Math.Min cuts a bank above the cap back to it at the next reset.
    /// </summary>
    private Task AfkTimeAsync(GameSession s, string[] args)
    {
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (verb == "info" && args.Length == 1) return GmChangeAsync(s, "time-info", "Wild AFK", null);
        if (verb is not ("set" or "add") || args.Length != 2) throw new ArgumentException("info | set <minutes> | add <minutes>");
        long seconds = checked(long.Parse(args[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) * SecondsPerMinute);
        return GmChangeAsync(s, "time", $"Bonus Time {verb} {args[1]} min", next =>
        {
            long bank = verb == "set" ? seconds : checked(next.Info.HighMultiplierTimeSeconds + seconds);
            next.Info.HighMultiplierTimeSeconds = Math.Max(0, bank);
            return null;
        });
    }

    private static string? SetMode(State next, bool on)
    {
        if (on && next.Info.HighMultiplierTimeSeconds <= 0) return "Bonus Time bank is empty: !afktime add <minutes> first.";
        next.Info.IsHighMagnification = on;
        if (!on) next.Info.HighMultiplierPausedAt = Now / 1000;
        return null;
    }

    /// <param name="edit">Changes a copy of the state and returns a refusal, or null to commit; null only reports.</param>
    private async Task GmChangeAsync(GameSession s, string origin, string label, Func<State, string?>? edit)
    {
        if (s.WildAfk is not { } state)
        {
            await GmCommands.ReplyAsync(s, "Wild AFK state is not loaded for this role.");
            return;
        }
        using var scope = await EnterStateScopeAsync(s, state, "gm-" + origin);
        if (_loop.Owners != null && scope == null) return;
        await state.Gate.WaitAsync();
        try
        {
            if (!Owns(s, state)) return;
            await ReconcileCoreAsync(s, Now);
            bool unlockedBefore = state.GmUnlocked;
            string? refusal = edit == null ? null : GmCommit(s, state, edit);
            if (edit != null && refusal == null) await GmPublishAsync(s, state, unlockedBefore);
            await GmCommands.ReplyAsync(s, refusal ?? $"{label}: {GmSummary(s, state)}");
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>Persist first, then the in-memory state (AGENTS.md); <see cref="State.Persisted"/> follows the write.</summary>
    private string? GmCommit(GameSession s, State state, Func<State, string?> edit)
    {
        var next = JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(state))!;
        if (edit(next) is { } refusal) return refusal;
        if (next.Info.HighMultiplierTimeSeconds <= 0)
        {
            next.Info.IsHighMagnification = false; // as ReconcileCoreAsync does for an exhausted bank
            next.RemainderMs = 0;
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next);
        _roles.SaveWildAfk(s.Role!.Id, bytes);
        state.Info = next.Info;
        state.RemainderMs = next.RemainderMs;
        state.GmUnlocked = next.GmUnlocked;
        state.Persisted = bytes;
        return null;
    }

    private async Task GmPublishAsync(GameSession s, State state, bool unlockedBefore)
    {
        await ReconcileCoreAsync(s, Now); // suspension follows the new bank, switch and unlock
        // 5056 is how the client learns a function opened or closed (FunctionService.Build); the OnHook panel follows it.
        if (state.GmUnlocked != unlockedBefore) await _functions.SyncAsync(s);
        s.Synced.Remove(Sync);
        await SyncAsync(s);
    }

    private string GmSummary(GameSession s, State state)
    {
        var info = state.Info;
        long bank = info.HighMultiplierTimeSeconds;
        static string Open(bool value) => value ? "open" : "locked";
        return string.Create(CultureInfo.InvariantCulture,
            $"bank {bank / SecondsPerMinute} min ({bank} s), switch {(info.IsHighMagnification ? "ON" : "OFF")}"
            + $"{(info.IsSuspended ? " (suspended)" : "")}, auto mode {Open(Unlocked(s))} (config {Open(_functions.ConfigOpen(s, Function))}, "
            + $"GM override {(state.GmUnlocked ? "on" : "off")}).");
    }
}
