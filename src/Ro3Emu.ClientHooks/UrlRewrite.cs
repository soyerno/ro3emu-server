using System;
using System.Diagnostics;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Sends the publisher's recharge / monthly-pass link to Konsor's donations page. OpenUrlPatch replaces the body
    /// of UnityEngine.Application.OpenURL(string) (a pure InternalCall extern in UnityEngine.CoreModule) with a call to
    /// <see cref="Open"/>, so every caller is caught at the one boundary they all pass through: the game, the SDK, and
    /// the reflection call Assembly-CSharp makes (its UTF-16 "OpenURL" literal).
    /// Frida on the live client proved the recharge button opens <c>https://console.playgenesis3.is/</c> through
    /// OpenURL -&gt; ShellExecuteW, that no network call fetches the URL (it is built client-side), and that the exact
    /// URL is not a plaintext literal anywhere in the client (so a static string rewrite cannot reach it). It also
    /// showed the button does not read the Lua <c>publicparamconfig.RechargeExternalUrl</c> table, so overriding that
    /// (the earlier attempt, kept in LuaPatches only as a harmless no-op) never changed the button.
    /// Because the extern's original icall binding is gone once the method has a managed body, <see cref="Open"/> opens
    /// the rewritten URL itself, through the same ShellExecute path the original used.
    /// </summary>
    public static class UrlRewrite
    {
        /// <summary>Where every publisher recharge / console link is sent instead.</summary>
        public const string Donations = "https://konsor.com.ar/donaciones";

        /// <summary>
        /// Rewrites a publisher recharge/console URL to Konsor's donations page; any other URL passes through unchanged.
        /// Origin-independent: it matches the publisher host by substring, so a runtime-built or decrypted URL is caught
        /// whatever its exact form.
        /// </summary>
        public static string Fix(string url)
        {
            // IndexOf, not Contains(string, StringComparison): that overload is not on every Unity Mono BCL; IndexOf is.
#pragma warning disable CA2249
            if (!string.IsNullOrEmpty(url) && url.IndexOf("playgenesis", StringComparison.OrdinalIgnoreCase) >= 0)
#pragma warning restore CA2249
                return Donations;
            return url;
        }

        /// <summary>
        /// The managed body OpenUrlPatch gives UnityEngine.Application.OpenURL: rewrite the URL, then open the result in
        /// the user's default browser ourselves (ShellExecute, the path the original icall ended in). Any failure is
        /// swallowed with a log line, exactly as the engine's own OpenURL does nothing visible on failure.
        /// </summary>
        public static void Open(string url)
        {
            url = Fix(url);
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                try { UnityEngine.Debug.LogWarning("[Ro3Emu.UrlRewrite] could not open " + url + ": " + e.GetType().Name + ": " + e.Message); }
                catch { /* logging must never throw out of OpenURL */ }
            }
        }
    }
}
