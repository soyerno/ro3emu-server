using System.Globalization;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>Where the Konsor rates come from: name -> value, empty when nothing is configured. Throws on a read error.</summary>
public interface IServerRatesSource
{
    IReadOnlyDictionary<string, double> Read();
}

/// <summary>
/// The konsor_rate table (name TEXT PRIMARY KEY, value REAL, updated_utc TEXT) of the realm database, which the admin
/// panel owns and writes. The server only reads it through a read-only handle and never creates it.
/// </summary>
public sealed class SqliteServerRatesSource(AccountStore accounts) : IServerRatesSource
{
    public const string Table = "konsor_rate";

    public IReadOnlyDictionary<string, double> Read()
    {
        var rows = new Dictionary<string, double>(StringComparer.Ordinal);
        using var db = accounts.Open("konsor/rates");
        using (var exists = db.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table";
            exists.Parameters.AddWithValue("$table", Table);
            if (exists.ExecuteScalar() == null) return rows;
        }
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT name, value FROM {Table}";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            // A value the panel should have refused (text, blob) is ignored: that rate stays 1.0.
            if (reader.GetFieldType(1) is var type && (type == typeof(double) || type == typeof(long)))
                rows[reader.GetString(0)] = Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture);
        }
        return rows;
    }
}

/// <summary>
/// Reads the Konsor rates once at startup and every <see cref="Interval"/> after, so a panel change applies without a
/// restart. A failed read keeps the last good values and warns once until a read succeeds again.
/// </summary>
public sealed class ServerRatesReader(ServerRates rates, IServerRatesSource source, ILogger<ServerRatesReader> log,
    TimeProvider time) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private bool _failing;

    /// <summary>One read; false when it failed and the previous values were kept.</summary>
    public bool Refresh()
    {
        IReadOnlyDictionary<string, double> rows;
        try { rows = source.Read(); }
        catch (Exception error) when (error is SqliteException or InvalidOperationException or IOException or ObjectDisposedException)
        {
            if (!_failing) log.LogWarning(error, "Server rates could not be read; keeping {Rates}", rates.Current);
            _failing = true;
            return false;
        }
        _failing = false;
        var before = rates.Current;
        rates.Apply(rows);
        if (rates.Current != before) log.LogInformation("Server rates now {Rates}", rates.Current);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Refresh();
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken)) Refresh();
    }
}
