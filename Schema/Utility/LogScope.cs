// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using log4net;

namespace Schema.Utility;

/// <summary>
/// The target a unit of work is about, held in log4net's logical context so it follows the unit's own awaits and tasks
/// and no other unit's. Structured log lines carry it as fields; <see cref="Prefix"/> is the text the same lines start
/// with in the plain format, which a structured line leaves out of its message.
/// </summary>
public static class LogScope
{
    internal const string Server = "SchemaSmith.server";
    internal const string Database = "SchemaSmith.database";
    internal const string Schema = "SchemaSmith.schema";
    internal const string Template = "SchemaSmith.template";
    internal const string Prefix = "SchemaSmith.prefix";

    private static readonly string[] Keys = [Server, Database, Schema, Template, Prefix];

    public static IDisposable Begin(string server, string database, string schema, string template, string prefix)
    {
        var previous = Array.ConvertAll(Keys, k => LogicalThreadContext.Properties[k]);
        Set(Server, server);
        Set(Database, database);
        Set(Schema, string.IsNullOrEmpty(schema) ? null : schema);
        Set(Template, template);
        Set(Prefix, prefix);
        return new Restore(previous);
    }

    internal static string Get(string key) => LogicalThreadContext.Properties[key] as string;

    private static void Set(string key, object value)
    {
        if (value == null) LogicalThreadContext.Properties.Remove(key);
        else LogicalThreadContext.Properties[key] = value;
    }

    private sealed class Restore(object[] previous) : IDisposable
    {
        public void Dispose()
        {
            for (var i = 0; i < Keys.Length; i++) Set(Keys[i], previous[i]);
        }
    }
}
