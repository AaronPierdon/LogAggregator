using System;
using System.Collections.Generic;

namespace LogAggregator.SampleData;

/// <summary>SIMULATION SUPPORT - one synthetic log record, independent of how it will later be
/// rendered (CSV columns vs. a flat-text line, which timestamp shape, which column order).
/// SampleLogGenerator turns a List&lt;RandomLogRecord&gt; into actual file text.</summary>
public sealed class RandomLogRecord
{
    public DateTime TimestampUtc { get; init; }
    public string Message { get; init; } = string.Empty;
    public string OtherData { get; init; } = string.Empty;
}

/// <summary>
/// SIMULATION SUPPORT - seeded random generator for synthetic log record content (timestamps,
/// messages, "other data" fields). Deterministic: the same seed always produces the same
/// records, so a simulation run's report is reproducible and a regression can be re-run
/// exactly. Flavor text (subsystem names, verbs) leans toward the kind of SCADA/industrial
/// logs the app was originally built around (see the /samples folder - Kepware, PI, Event
/// Viewer) purely for realism; it has no effect on parsing correctness.
/// </summary>
public sealed class RandomDataFactory
{
    private readonly Random _rng;

    private static readonly string[] Severities = { "INFO", "WARN", "ERROR", "DEBUG" };

    private static readonly string[] Subsystems =
    {
        "Kepware.Gateway", "PI.Collector", "EventViewer.Security", "SCADA.PLC01",
        "Modbus.Bridge", "OPCUA.Client", "Historian.Sync", "Alarm.Manager"
    };

    private static readonly string[] Verbs =
    {
        "Connection established", "Connection lost", "Tag value updated",
        "Threshold exceeded", "Heartbeat received", "Configuration reloaded",
        "Buffer flushed", "Retry attempt failed", "Session authenticated",
        "Watchdog reset", "Scan cycle completed", "Deadband suppressed an update"
    };

    private static readonly string[] Units = { "psi", "degC", "rpm", "pct", "V", "A", "Hz" };

    public RandomDataFactory(int seed)
    {
        _rng = new Random(seed);
    }

    /// <summary>
    /// Creates <paramref name="count"/> records with strictly increasing timestamps starting
    /// at <paramref name="startUtc"/>, spaced by a random jitter around
    /// <paramref name="avgInterval"/>. When <paramref name="forceYear"/> is set, every
    /// timestamp's year is overridden to that value (used for formats with no year in the
    /// text - see SampleTimestampFormat.HasYear) while month/day/time-of-day stay random.
    /// </summary>
    public List<RandomLogRecord> CreateRecords(int count, DateTime startUtc, TimeSpan avgInterval, int? forceYear = null)
    {
        var records = new List<RandomLogRecord>(count);
        var current = startUtc;

        for (int i = 0; i < count; i++)
        {
            var jitterMs = _rng.Next(-(int)(avgInterval.TotalMilliseconds * 0.4), (int)(avgInterval.TotalMilliseconds * 0.4) + 1);
            current = current.AddMilliseconds(avgInterval.TotalMilliseconds + jitterMs);
            // Sub-millisecond ticks too, so 7-digit-fraction formats have something real to
            // carry instead of always ending in ".0000000".
            current = current.AddTicks(_rng.Next(0, 10_000));

            // AddYears (not manual month/day/year reconstruction) so a value that happens to
            // land on Feb 29 can't throw when forced into a non-leap year - AddYears rolls
            // Feb 29 -> Feb 28 in that case, per documented DateTime behavior, instead of
            // crashing the generator.
            var ts = forceYear is int y ? current.AddYears(y - current.Year) : current;

            records.Add(new RandomLogRecord
            {
                TimestampUtc = DateTime.SpecifyKind(ts, DateTimeKind.Utc),
                Message = RandomMessage(),
                OtherData = RandomOtherData()
            });
        }

        return records;
    }

    private string RandomMessage()
    {
        var severity = Severities[_rng.Next(Severities.Length)];
        var subsystem = Subsystems[_rng.Next(Subsystems.Length)];
        var verb = Verbs[_rng.Next(Verbs.Length)];
        return $"{severity} {subsystem}: {verb}";
    }

    private string RandomOtherData()
    {
        var tag = $"tag{_rng.Next(1, 64):D2}";
        var value = _rng.NextDouble() * 1000.0;
        var unit = Units[_rng.Next(Units.Length)];
        return $"tag={tag} value={value:F2} unit={unit}";
    }
}
