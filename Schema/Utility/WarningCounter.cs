// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Threading;
using log4net.Appender;
using log4net.Core;

namespace Schema.Utility;

/// <summary>
/// Counts the warnings (and anything more severe) a run logs, so <c>ExitNonZeroOnWarning</c> can turn a run that would
/// exit 0 into exit 1.
/// </summary>
public sealed class WarningCounter : AppenderSkeleton
{
    private static int _count;

    public static int Count => Volatile.Read(ref _count);

    internal static void Reset() => Interlocked.Exchange(ref _count, 0);

    protected override void Append(LoggingEvent loggingEvent)
    {
        if (loggingEvent.Level >= Level.Warn)
            Interlocked.Increment(ref _count);
    }
}
