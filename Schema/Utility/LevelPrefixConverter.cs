// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
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

    /// <summary>
    /// A server message that says <c>WARNING: </c> itself, ready to log at warning level: the layout puts the prefix at
    /// the front of the line, so the message's own is dropped. Its indentation stays.
    /// </summary>
    public static string WithoutWarningPrefix(string message)
    {
        if (message == null) return null;
        var start = message.Length - message.TrimStart().Length;
        return message.AsSpan(start).StartsWith("WARNING: ", StringComparison.Ordinal) ? message[..start] + message[(start + 9)..] : message;
    }
}
