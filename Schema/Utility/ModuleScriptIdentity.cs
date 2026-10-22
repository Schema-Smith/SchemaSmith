// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Schema.Domain;

namespace Schema.Utility;

public enum ModuleKind { View, Function, Procedure, Trigger, Event }

/// <summary>
/// The one module a script creates, read from its CREATE statement (or, on SQL Server, the ALTER that carries the body
/// in the legacy guarded-stub form), so the deploy can ask the engine how it renders that object. A script with no
/// such statement, with more than one, or creating something the engine's rendering does not cover has no identity
/// and always runs. String literals are blanked first, so a stub inside EXEC('...') is not a second statement.
/// </summary>
public sealed class ModuleScriptIdentity
{
    public ModuleKind Kind { get; init; }
    /// <summary>The object's schema; null on the MySQL family, where the database is the schema.</summary>
    public string Schema { get; init; }
    public string Name { get; init; }
    /// <summary>For a trigger: the table it is on, which is also where its schema comes from on PostgreSQL.</summary>
    public string TableSchema { get; init; }
    public string TableName { get; init; }

    private const string SqlServerToken = @"(\[[^\]]+\]|""[^""]+""|[\w$#@]+)";
    private const string PostgreSqlToken = @"(""[^""]+""|[\w$]+)";
    private const string MySqlToken = @"(`[^`]+`|""[^""]+""|[\w$]+)";

    private static readonly Regex SqlServerCreate = new(
        $@"(?<!\w)(?:CREATE\s+(?:OR\s+ALTER\s+)?|ALTER\s+)(?<kind>PROC|PROCEDURE|FUNCTION|VIEW|TRIGGER)\s+(?<name>{SqlServerToken}(\s*\.\s*{SqlServerToken})?)(?<rest>[\s\S]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PostgreSqlCreate = new(
        $@"(?<!\w)CREATE\s+(OR\s+REPLACE\s+)?(CONSTRAINT\s+)?(?<kind>PROCEDURE|FUNCTION|VIEW|TRIGGER)\s+(?<name>{PostgreSqlToken}(\s*\.\s*{PostgreSqlToken})?)(?<rest>[\s\S]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MySqlCreate = new(
        $@"(?<!\w)CREATE\s+(OR\s+REPLACE\s+)?(ALGORITHM\s*=\s*\w+\s+)?(DEFINER\s*=\s*\S+\s+)?(SQL\s+SECURITY\s+\w+\s+)?(AGGREGATE\s+)?(?<kind>PROCEDURE|FUNCTION|VIEW|TRIGGER|EVENT)\s+(?<name>{MySqlToken}(\s*\.\s*{MySqlToken})?)(?<rest>[\s\S]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StringLiteral = new(@"N?'(?:[^']|'')*'", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex SqlServerDrop = new(
        $@"(?<!\w)DROP\s+(?<kind>PROC|PROCEDURE|FUNCTION|VIEW|TRIGGER)\s+(?:IF\s+EXISTS\s+)?(?<name>{SqlServerToken}(\s*\.\s*{SqlServerToken})?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PostgreSqlDrop = new(
        $@"(?<!\w)DROP\s+(?<kind>PROCEDURE|FUNCTION|VIEW|TRIGGER)\s+(?:IF\s+EXISTS\s+)?(?<name>{PostgreSqlToken}(\s*\.\s*{PostgreSqlToken})?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MySqlDrop = new(
        $@"(?<!\w)DROP\s+(?<kind>PROCEDURE|FUNCTION|VIEW|TRIGGER|EVENT)\s+(?:IF\s+EXISTS\s+)?(?<name>{MySqlToken}(\s*\.\s*{MySqlToken})?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ModuleScriptIdentity Parse(IReadOnlyList<string> batches, Platform platform)
    {
        var basePlatform = platform.GetBasePlatform();
        var pattern = basePlatform switch
        {
            Platform.SqlServer => SqlServerCreate,
            Platform.PostgreSQL => PostgreSqlCreate,
            Platform.MySQL => MySqlCreate,
            _ => null
        };
        if (pattern == null) return null;

        var matches = batches
            .Select(b => StringLiteral.Replace(b ?? "", m => new string(' ', m.Length)))
            .SelectMany(b => pattern.Matches(b).Cast<Match>())
            .ToList();
        if (matches.Count != 1) return null;

        var match = matches[0];
        var kind = KindOf(match.Groups["kind"].Value);
        var (schema, name) = SplitName(match.Groups["name"].Value, basePlatform);
        string tableSchema = null, tableName = null;
        if (kind == ModuleKind.Trigger)
        {
            var on = Regex.Match(match.Groups["rest"].Value, $@"\bON\s+(?<table>{(basePlatform == Platform.SqlServer ? SqlServerToken : basePlatform == Platform.PostgreSQL ? PostgreSqlToken : MySqlToken)}(\s*\.\s*{(basePlatform == Platform.SqlServer ? SqlServerToken : basePlatform == Platform.PostgreSQL ? PostgreSqlToken : MySqlToken)})?)", RegexOptions.IgnoreCase);
            if (on.Success) (tableSchema, tableName) = SplitName(on.Groups["table"].Value, basePlatform);
            // A PostgreSQL trigger lives in its table's schema and is never schema-qualified itself.
            if (basePlatform == Platform.PostgreSQL) schema = tableSchema;
        }

        return new ModuleScriptIdentity { Kind = kind, Schema = schema, Name = name, TableSchema = tableSchema, TableName = tableName };
    }

    /// <summary>
    /// True when the script drops the module it creates. Such a script takes the module's grants with it, so the
    /// quench captures them before it runs and puts them back after. String literals are left in on purpose: a DROP
    /// inside an EXEC string still drops, and capturing when nothing was lost costs nothing, while missing a drop
    /// loses the grants.
    /// </summary>
    public static bool DropsModule(IReadOnlyList<string> batches, Platform platform)
    {
        var identity = Parse(batches, platform);
        if (identity == null) return false;
        var basePlatform = platform.GetBasePlatform();
        var pattern = basePlatform switch
        {
            Platform.SqlServer => SqlServerDrop,
            Platform.PostgreSQL => PostgreSqlDrop,
            Platform.MySQL => MySqlDrop,
            _ => null
        };
        if (pattern == null) return false;
        // A quoted PostgreSQL identifier keeps its case; the other engines do not distinguish.
        var comparison = basePlatform == Platform.PostgreSQL ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return batches.SelectMany(b => pattern.Matches(b ?? "").Cast<Match>())
            .Any(m => KindOf(m.Groups["kind"].Value) == identity.Kind
                      && string.Equals(SplitName(m.Groups["name"].Value, basePlatform).Name, identity.Name, comparison));
    }

    private static ModuleKind KindOf(string word) => word.ToUpperInvariant() switch
    {
        "PROC" or "PROCEDURE" => ModuleKind.Procedure,
        "FUNCTION" => ModuleKind.Function,
        "VIEW" => ModuleKind.View,
        "TRIGGER" => ModuleKind.Trigger,
        _ => ModuleKind.Event
    };

    private static (string Schema, string Name) SplitName(string qualified, Platform basePlatform)
    {
        var parts = Regex.Split(qualified.Trim(), @"\s*\.\s*(?=(?:\[|""|`|[\w$#@]))").Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        var name = Unquote(parts[^1], basePlatform);
        var schema = parts.Length > 1 ? Unquote(parts[^2], basePlatform) : basePlatform switch
        {
            Platform.SqlServer => "dbo",
            Platform.PostgreSQL => "public",
            _ => null
        };
        return (schema, name);
    }

    private static string Unquote(string token, Platform basePlatform)
    {
        token = token.Trim();
        if (token.Length >= 2 && ((token[0] == '[' && token[^1] == ']') || (token[0] == '"' && token[^1] == '"') || (token[0] == '`' && token[^1] == '`')))
            return token[1..^1].Replace("]]", "]").Replace("\"\"", "\"").Replace("``", "`");
        // An unquoted PostgreSQL identifier folds to lower case; the other engines keep it as written.
        return basePlatform == Platform.PostgreSQL ? token.ToLowerInvariant() : token;
    }
}
