// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.IO;
using log4net.Core;
using log4net.Layout.Pattern;

namespace Schema.Utility;

/// <summary>
/// <c>%levelPrefix</c> in a log layout: <c>WARNING: </c> or <c>ERROR: </c> before a line logged at that level, nothing
/// before any other. A redirected console loses its colours, and then the prefix is the only way to tell a warning from
/// progress.
/// </summary>
public sealed class LevelPrefixConverter : PatternLayoutConverter
{
    protected override void Convert(TextWriter writer, LoggingEvent loggingEvent) =>
        writer.Write(For(loggingEvent.Level));

    internal static string For(Level level) =>
        level == null || level < Level.Warn ? "" : level < Level.Error ? "WARNING: " : "ERROR: ";
}
