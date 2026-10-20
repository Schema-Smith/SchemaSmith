// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Schema.Domain;

namespace SchemaTongs;

public static class CleanupScriptGenerator
{
    public static string GenerateDropStatement(string fileName, ScriptObjectType objectType, Platform platform, string folderName = null)
    {
        var dropKeyword = GetDropKeyword(objectType, folderName);
        if (dropKeyword == null) return null;

        var objectName = ParseObjectName(fileName, platform);
        if (objectName == null) return null;

        var quotedName = QuoteIdentifier(objectName, platform);
        return $"{dropKeyword} IF EXISTS {quotedName};";
    }

    public static string GenerateSummaryHeader(int count)
    {
        return $"-- {count} orphaned objects detected during extraction on {DateTime.Now:yyyy-MM-dd}";
    }

    public static string GenerateCleanupScript(List<string> fileNames, ScriptObjectType objectType, Platform platform, string folderName = null)
    {
        var statements = fileNames
            .Select(f => GenerateDropStatement(f, objectType, platform, folderName))
            .Where(s => s != null)
            .ToList();

        if (statements.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine(GenerateSummaryHeader(statements.Count));
        sb.AppendLine();
        foreach (var statement in statements)
            sb.AppendLine(statement);

        return sb.ToString();
    }

    private static string GetDropKeyword(ScriptObjectType objectType, string folderName)
    {
        if (objectType == ScriptObjectType.None)
            return GetDropKeywordForJsonFolder(folderName);

        return objectType switch
        {
            ScriptObjectType.Views => "DROP VIEW",
            ScriptObjectType.Functions => "DROP FUNCTION",
            ScriptObjectType.Procedures => "DROP PROCEDURE",
            ScriptObjectType.Triggers => "DROP TRIGGER",
            ScriptObjectType.DDLTriggers => "DROP TRIGGER",
            ScriptObjectType.DataTypes => "DROP TYPE",
            ScriptObjectType.DomainTypes => "DROP DOMAIN",
            ScriptObjectType.EnumTypes => "DROP TYPE",
            ScriptObjectType.CompositeTypes => "DROP TYPE",
            ScriptObjectType.TriggerFunctions => "DROP FUNCTION",
            ScriptObjectType.WindowFunctions => "DROP FUNCTION",
            ScriptObjectType.Aggregates => "DROP AGGREGATE",
            ScriptObjectType.Sequences => "DROP SEQUENCE",
            ScriptObjectType.Rules => "DROP RULE",
            ScriptObjectType.Events => "DROP EVENT",
            ScriptObjectType.FullTextCatalogs => "DROP FULLTEXT CATALOG",
            ScriptObjectType.FullTextStopLists => "DROP FULLTEXT STOPLIST",
            ScriptObjectType.XMLSchemaCollections => "DROP XML SCHEMA COLLECTION",
            ScriptObjectType.Synonyms => "DROP SYNONYM",
            ScriptObjectType.SecurityPolicies => "DROP SECURITY POLICY",
            ScriptObjectType.SchemaBoundViews => "DROP VIEW",
            ScriptObjectType.SchemaBoundFunctions => "DROP FUNCTION",
            ScriptObjectType.Collations => "DROP COLLATION",
            ScriptObjectType.Publications => "DROP PUBLICATION",
            ScriptObjectType.RangeTypes => "DROP TYPE",
            ScriptObjectType.TextSearchConfigurations => "DROP TEXT SEARCH CONFIGURATION",
            ScriptObjectType.EventTriggers => "DROP EVENT TRIGGER",
            // The DROP needs the argument types, which the file name does not carry in a form that can be read back.
            ScriptObjectType.Operators => null,
            ScriptObjectType.Casts => null,
            ScriptObjectType.Schemas => null,
            _ => null
        };
    }

    private static string GetDropKeywordForJsonFolder(string folderName)
    {
        return folderName switch
        {
            "Tables" => "DROP TABLE",
            "Indexed Views" => "DROP VIEW",
            "Materialized Views" => "DROP MATERIALIZED VIEW",
            _ => null
        };
    }

    private static (string Schema, string Name)? ParseObjectName(string fileName, Platform platform)
    {
        var ext = Path.GetExtension(fileName);
        if (!ext.Equals(".sql", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".sqlerror", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return null;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(baseName)) return null;

        if (platform.GetBasePlatform() == Platform.MySQL)
            return (null, baseName);

        var dotIndex = baseName.IndexOf('.');
        if (dotIndex > 0 && dotIndex < baseName.Length - 1)
            return (baseName[..dotIndex], baseName[(dotIndex + 1)..]);

        // Single-part name (e.g., DDL triggers without schema prefix)
        return (null, baseName);
    }

    private static string QuoteIdentifier((string Schema, string Name)? parsed, Platform platform)
    {
        if (parsed == null) return null;
        var (schema, name) = parsed.Value;

        return platform.GetBasePlatform() switch
        {
            Platform.SqlServer => schema != null ? $"[{schema}].[{name}]" : $"[{name}]",
            Platform.PostgreSQL => schema != null ? $"\"{schema}\".\"{name}\"" : $"\"{name}\"",
            Platform.MySQL => $"`{name}`",
            _ => throw new ArgumentException($"Unsupported platform: {platform}")
        };
    }
}
