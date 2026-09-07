namespace Momus.Server.Store;

/// <summary>An application that has reported at least once.</summary>
public sealed record StoredApp
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Environment { get; init; }

    /// <summary>Newest version seen. A change here is a deploy marker (M3).</summary>
    public string? Version { get; init; }

    public required DateTimeOffset FirstSeen { get; init; }
    public required DateTimeOffset LastSeen { get; init; }

    /// <summary>How many processes have reported under this name — two replicas are two rows.</summary>
    public int InstanceCount { get; init; }
}

/// <summary>
/// One statement as the application sees it, summed over a stretch of time. The database's own
/// view of the same statement is a separate read joined on <see cref="Fingerprint"/>.
/// </summary>
public sealed record AppQueryStat
{
    public required string Fingerprint { get; init; }
    public string TargetId { get; init; } = "";

    /// <summary>The operation that runs it most often; the rest are counted in <see cref="OperationCount"/>.</summary>
    public required string Operation { get; init; }
    public int OperationCount { get; init; }

    public string? CallSite { get; init; }
    public string? Sample { get; init; }

    public long Calls { get; init; }
    public double DurationSum { get; init; }
    public double DurationMax { get; init; }
    public long Rows { get; init; }

    /// <summary>Worst repeats of this statement inside a single operation: the N in N+1.</summary>
    public int MaxRepeats { get; init; }

    public long Errors { get; init; }

    /// <summary>Span the numbers cover, so calls per minute means something.</summary>
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }

    public double MeanMs => Calls == 0 ? 0 : DurationSum / Calls;

    /// <summary>
    /// Calls per minute since this statement was first seen in the stretch being shown — not over
    /// the windows it appears in. A statement that ran once, five minutes ago, ran once in five
    /// minutes; dividing by the five-second window it landed in would call that twelve a minute.
    /// </summary>
    public double CallsPerMinute
    {
        get
        {
            var minutes = (DateTimeOffset.UtcNow - From).TotalMinutes;
            return minutes <= 0 ? Calls : Calls / minutes;
        }
    }
}

/// <summary>One named unit of work — a route or a background job — summed over a stretch of time.</summary>
public sealed record AppOperationStat
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public long Calls { get; init; }
    public double DurationSum { get; init; }
    public double DurationMax { get; init; }
    public long QuerySum { get; init; }
    public long QueryMax { get; init; }
    public double DbMs { get; init; }

    public double MeanMs => Calls == 0 ? 0 : DurationSum / Calls;
    public double MeanQueries => Calls == 0 ? 0 : (double)QuerySum / Calls;

    /// <summary>Share of the operation's own time spent waiting on the database.</summary>
    public double DbShare => DurationSum <= 0 ? 0 : DbMs / DurationSum;
}

/// <summary>The newest window an app posted, for the "is the client still sending" pill.</summary>
public sealed record StoredWindow
{
    public required long Id { get; init; }
    public required string AppId { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public string? Version { get; init; }
    public long Overflow { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
}
