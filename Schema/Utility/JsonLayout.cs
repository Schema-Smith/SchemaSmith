// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Globalization;
using System.IO;
using log4net.Core;
using log4net.Layout;
using Newtonsoft.Json;

namespace Schema.Utility;

/// <summary>
/// One JSON object per log line (LogFormat: json), with the names log platforms recognise without configuration
/// (timestamp, level, message) and the run and the target as fields rather than as text in the message.
/// </summary>
public sealed class JsonLayout : LayoutSkeleton
{
    public JsonLayout() => IgnoresException = false;

    public override void ActivateOptions() { }

    public override void Format(TextWriter writer, LoggingEvent loggingEvent)
    {
        writer.Write(Render(loggingEvent));
        writer.Write('\n');
    }

    public static string Render(LoggingEvent loggingEvent)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var json = new JsonTextWriter(text) { Formatting = Formatting.None })
        {
            json.WriteStartObject();
            Write(json, "timestamp", loggingEvent.TimeStampUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            Write(json, "level", Level(loggingEvent.Level));
            Write(json, "message", WithoutScopePrefix(loggingEvent.RenderedMessage ?? ""));
            Write(json, "tool", RunContext.Tool);
            Write(json, "runId", RunContext.Id.ToString());
            Write(json, "logger", Logger(loggingEvent.LoggerName));
            WriteIfSet(json, "server", LogScope.Get(LogScope.Server));
            WriteIfSet(json, "database", LogScope.Get(LogScope.Database));
            WriteIfSet(json, "schema", LogScope.Get(LogScope.Schema));
            WriteIfSet(json, "template", LogScope.Get(LogScope.Template));
            WriteIfSet(json, "error", loggingEvent.GetExceptionString());
            json.WriteEndObject();
        }
        return text.ToString();
    }

    private static void Write(JsonWriter json, string name, string value)
    {
        json.WritePropertyName(name);
        json.WriteValue(value);
    }

    private static void WriteIfSet(JsonWriter json, string name, string value)
    {
        if (!string.IsNullOrEmpty(value)) Write(json, name, value);
    }

    private static string WithoutScopePrefix(string message)
    {
        var prefix = LogScope.Get(LogScope.Prefix);
        return !string.IsNullOrEmpty(prefix) && message.StartsWith(prefix, StringComparison.Ordinal)
            ? message[prefix.Length..].TrimStart(' ')
            : message;
    }

    private static string Level(Level level) =>
        level == null ? "info"
        : level >= log4net.Core.Level.Error ? "error"
        : level >= log4net.Core.Level.Warn ? "warn"
        : level >= log4net.Core.Level.Info ? "info"
        : "debug";

    private static string Logger(string name) => name switch
    {
        "ProgressLog" => "progress",
        "ErrorLog" => "error",
        "FailureLog" => "failure",
        _ => name
    };
}
