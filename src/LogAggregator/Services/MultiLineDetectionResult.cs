using System;
using System.Collections.Generic;
using LogAggregator.Models;

namespace LogAggregator.Services;

/// <summary>One plausible timestamp location, found while scanning real sample lines.</summary>
public class TimestampCandidate
{
    public TimestampProfile Profile { get; set; } = new();
    public string Description { get; set; } = string.Empty;
    public string ExampleRawText { get; set; } = string.Empty;
    public DateTime ExampleUtc { get; set; }
    public double MatchRate { get; set; }
}

public enum DetectionStatus
{
    /// <summary>Detection hasn't run yet (e.g. no files chosen).</summary>
    Pending,
    /// <summary>Exactly one consistent candidate was found - safe to auto-apply.</summary>
    Confident,
    /// <summary>Two or more equally plausible candidates were found - ask the user to pick.</summary>
    Ambiguous,
    /// <summary>Nothing consistent was found - fall back to manual entry.</summary>
    Failed
}

public class MultiLineDetectionResult
{
    public DetectionStatus Status { get; set; } = DetectionStatus.Pending;
    public List<TimestampCandidate> Candidates { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}
