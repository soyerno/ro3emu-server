using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Invented content: a friendly heal's 31101 must not carry the show's hit reaction.</summary>
public sealed class SkillHealShowPointTests
{
    private const int Show = 9917, SkillId = 99170001, Monster = 16320001;
    private const int ReactionEffect = Show * 100, QuietEffect = Show * 100 + 1;

    [Fact]
    public async Task HealHitCarriesAShowPointWithoutHitReaction()
    {
        await using var f = new Fixture();
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Healer");
        await f.Loop.InvokeAsync(() => f.Character.SetHpSpAsync(f.Session, 1, null));
        await f.Loop.InvokeAsync(() => f.Combat.CastExtraOnAlliesAsync(f.Session, f.Row, [f.Session]));
        var heal = await NextHit(bot, h => h.Dsts.Any(d => d.DamageType == DamageType.Heal));
        Assert.All(heal.Dsts, d => Assert.Equal(DamageType.Heal, d.DamageType));
        Assert.Equal(QuietEffect, heal.ShowPoint);
    }

    [Fact]
    public async Task DamageHitKeepsTheHitReactionShowPoint()
    {
        await using var f = new Fixture();
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Striker");
        var target = await f.Loop.InvokeAsync(f.SpawnMonster);
        await f.Loop.InvokeAsync(() => f.Combat.CastExtraAsync(f.Session, f.Row, [target]));
        var hit = await NextHit(bot, h => h.Dsts.Any(d => d.DstID == target.Uid));
        Assert.DoesNotContain(hit.Dsts, d => d.DamageType == DamageType.Heal);
        Assert.Equal(ReactionEffect, hit.ShowPoint);
    }

    private static async Task<SkillHitInfo> NextHit(BotClient bot, Func<SkillHitInfo, bool> match)
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body) is { SkillId: SkillId } h && match(h), TimeSpan.FromSeconds(5));
        return SkillHitInfo.Parser.ParseFrom(packet.Body);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-healshow-" + Guid.NewGuid().ToString("N"));
        public SyntheticHost Host { get; private set; } = null!;
        public GameSession Session => Host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        public GameLoop Loop => Host.Service<GameLoop>();
        public CharacterService Character => Host.Service<CharacterService>();
        public CombatService Combat => Host.Service<CombatService>();

        /// <summary>A skill that both damages (P1[0]) and heals its friendly recipients (P2[0]).</summary>
        public JsonElement Row { get; } = JsonSerializer.SerializeToElement(new
        {
            _iID = SkillId, _iSkillID = SkillId / 100, _iScriptShow = Show, _kScriptLogic = "SyntheticHeal",
            _kDamageParam1 = new[] { 10000 }, _kDamageParam2 = new[] { 50 },
        });

        public async Task Start()
        {
            foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(_root, Path.GetRelativePath(SyntheticData.Dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
            Directory.CreateDirectory(Path.Combine(_root, "skills"));
            // The show's first entry plays a hit reaction; its second has a damage part without one.
            File.WriteAllText(Path.Combine(_root, "skills", "Lua_Config_EffectData.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [ReactionEffect.ToString(CultureInfo.InvariantCulture)] = new { m_kDamage = new { m_kActionDamge = new { m_iHitTime = 1 } } },
                [QuietEffect.ToString(CultureInfo.InvariantCulture)] = new { m_kDamage = new { m_iHitTime = 1 } },
            }));
            Host = await SyntheticHost.StartAsync(o => o.GameDataDir = _root);
        }

        public async Task<MonsterUnit> SpawnMonster()
        {
            var monsters = Host.Service<MonsterService>();
            var template = monsters.Template(Monster) ?? throw new InvalidOperationException("Missing synthetic monster.");
            return await monsters.SpawnSingleAsync(Session.SceneEntry, Monster,
                new MapPoint(Session.Position.X, Session.Position.Y, Session.Position.Z, 0), Session.InstanceId,
                customTemplate: template with { SpeedCm = 250, RoamDistance = 0 })
                ?? throw new InvalidOperationException("Synthetic monster could not spawn.");
        }

        public async ValueTask DisposeAsync()
        {
            if (Host != null) await Host.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }
}
