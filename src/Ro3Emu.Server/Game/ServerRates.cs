namespace Ro3Emu.Server.Game;

/// <summary>
/// Konsor server rates (not original behavior: the client has no rate setting). The admin panel writes them to the
/// konsor_rate table of the realm database and <see cref="ServerRatesReader"/> re-reads it; a missing table or row is
/// 1.0, every value is clamped to 0.1-10.0. At 1.0 each scale returns its input unchanged.
/// Rounding: a scaled amount is floor(amount x rate) in decimal arithmetic, at least 1 when the amount is positive.
/// </summary>
public sealed class ServerRates
{
    public const string BaseExpName = "base_exp", JobExpName = "job_exp", QuestExpName = "quest_exp", DropName = "drop",
        ZenyName = "zeny";
    public const double Min = 0.1, Max = 10.0, Default = 1.0;
    /// <summary>ItemConfig 1 is Zeny (CharacterService.ZenyId; RoleStore's currency map, 1 = Zeny).</summary>
    public const int ZenyItem = 1;
    /// <summary>DropPoolConfig slot chances are per million (RewardService.RollPool).</summary>
    public const double ChanceScale = 1_000_000;

    /// <summary>One consistent set of rates; a reward resolution reads one snapshot.</summary>
    public sealed record Values(double BaseExp, double JobExp, double QuestExp, double Drop, double Zeny)
    {
        public static Values Default { get; } = new(1.0, 1.0, 1.0, 1.0, 1.0);

        /// <summary>Monster kill EXP: Base EXP by base_exp and Job EXP (equal to it before scaling) by job_exp.</summary>
        public (long Base, long Job) KillExp(long exp) => (Scale(exp, BaseExp), Scale(exp, JobExp));

        /// <summary>Quest, task and award EXP (Base and Job alike) by quest_exp.</summary>
        public long AwardExp(long exp) => Scale(exp, QuestExp);

        /// <summary>A drop slot's chance per million by drop, capped at certainty; never more rolls.</summary>
        public double DropChance(double chance) => Drop == 1.0 ? chance : Math.Min(ChanceScale, chance * Drop);

        /// <summary>Zeny entries added to <paramref name="drops"/> from index <paramref name="from"/> on, by zeny.</summary>
        public void ScaleZeny(List<(int Entry, int Count)> drops, int from)
        {
            if (Zeny == 1.0) return;
            for (int i = from; i < drops.Count; i++)
                if (drops[i].Entry == ZenyItem) drops[i] = (ZenyItem, Scale(drops[i].Count, Zeny));
        }
    }

    private Values _current = Values.Default;

    public Values Current => Volatile.Read(ref _current);

    /// <summary>Replaces every rate with the table's rows: unknown names are ignored, missing ones are 1.0.</summary>
    public void Apply(IReadOnlyDictionary<string, double> rows)
    {
        double Rate(string name) => rows.TryGetValue(name, out var value) ? Clamp(value) : Default;
        Volatile.Write(ref _current, new Values(Rate(BaseExpName), Rate(JobExpName), Rate(QuestExpName), Rate(DropName),
            Rate(ZenyName)));
    }

    /// <summary>0.1-10.0; a non-finite value counts as unset.</summary>
    public static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, Min, Max) : Default;

    public static long Scale(long amount, double rate)
    {
        if (amount <= 0 || rate == 1.0) return amount;
        decimal scaled = decimal.Floor(amount * (decimal)Clamp(rate));
        return scaled >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)scaled);
    }

    public static int Scale(int amount, double rate) => (int)Math.Min(int.MaxValue, Scale((long)amount, rate));
}
