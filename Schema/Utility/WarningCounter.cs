// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using log4net.Appender;
using log4net.Core;

namespace Schema.Utility;

/// <summary>
/// Counts the warnings (and anything more severe) a run logs, so <c>ExitNonZeroOnWarning</c> can turn a run that would
/// exit 0 into exit 1, and keeps the first of them so that exit can say which they were.
/// </summary>
public sealed class WarningCounter : AppenderSkeleton
{
    public const int KeptWarnings = 50;

    private static int _count;
    private static ConcurrentQueue<string> _kept = new();

    public static int Count => Volatile.Read(ref _count);

    /// <summary>The first <see cref="KeptWarnings"/> warnings, each its first line with its level in front.</summary>
    public static IReadOnlyCollection<string> Kept => _kept.ToArray();

    internal static void Reset()
    {
        Interlocked.Exchange(ref _count, 0);
        Interlocked.Exchange(ref _kept, new ConcurrentQueue<string>());
    }

    protected override void Append(LoggingEvent loggingEvent)
    {
        if (loggingEvent.Level < Level.Warn) return;
        if (Interlocked.Increment(ref _count) > KeptWarnings) return;
        var message = loggingEvent.RenderedMessage ?? "";
        var lineEnd = message.IndexOfAny(['\r', '\n']);
        _kept.Enqueue(LevelPrefixConverter.For(loggingEvent.Level) + (lineEnd < 0 ? message : message[..lineEnd]));
    }
}
