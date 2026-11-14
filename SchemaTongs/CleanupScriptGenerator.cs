// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Schema.Domain;
using Schema.Utility;

namespace SchemaTongs;

public static class CleanupScriptGenerator
{
    public static string GenerateDropStatement(string fileName, ScriptObjectType objectType, Platform platform, string folderName = null)
    {
        var dropKeyword = GetDropKeyword(objectType, folderName);
        if (dropKeyword == null) return null;

        var quotedName = ObjectReference(fileName, objectType, platform);
        if (quotedName == null) return null;

        // Before MariaDB 11.4, PACKAGE is a keyword only under ORACLE mode.
        if (objectType == ScriptObjectType.Packages)
            return $"SET @ss_saved_sql_mode = @@SESSION.sql_mode; SET SESSION sql_mode = 'ORACLE'; {dropKeyword} IF EXISTS {quotedName}; SET SESSION sql_mode = @ss_saved_sql_mode;";
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
            ScriptObjectType.Packages => "DROP PACKAGE",
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

    // The object a file was written for, as the drop names it. Each part of the file name is decoded (FileNameEncoder wrote
    // it) and quoted with its delimiter escaped. A table trigger's file is schema.table.trigger, and PostgreSQL drops a
    // trigger on its table; an overloaded routine's file ends with its argument types, which name the one to drop. A
    // name that itself holds a dot cannot be told from the separator, so the first dot is taken as the schema's.
    private static string ObjectReference(string fileName, ScriptObjectType objectType, Platform platform)
    {
        var ext = Path.GetExtension(fileName);
        if (!ext.Equals(".sql", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".sqlerror", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return null;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(baseName)) return null;

        var basePlatform = platform.GetBasePlatform();
        if (basePlatform is not (Platform.SqlServer or Platform.PostgreSQL or Platform.MySQL))
            throw new ArgumentException($"Unsupported platform: {platform}");
        string Quote(string encoded)
        {
            var name = Identifier.EscapeDelimited(FileNameEncoder.Decode(encoded), basePlatform);
            return basePlatform switch
            {
                Platform.SqlServer => $"[{name}]",
                Platform.PostgreSQL => $"\"{name}\"",
                _ => $"`{name}`"
            };
        }

        if (basePlatform == Platform.MySQL)
            return Quote(baseName);

        var arguments = "";
        var paren = baseName.IndexOf('(');
        if (paren > 0 && baseName.EndsWith(')'))
        {
            arguments = FileNameEncoder.Decode(baseName[paren..]);
            baseName = baseName[..paren];
        }

        var parts = baseName.Split('.');
        if (objectType == ScriptObjectType.Triggers && parts.Length == 3)
            return basePlatform == Platform.PostgreSQL
                ? $"{Quote(parts[2])} ON {Quote(parts[0])}.{Quote(parts[1])}"
                : $"{Quote(parts[0])}.{Quote(parts[2])}";

        var dot = baseName.IndexOf('.');
        var reference = dot > 0 && dot < baseName.Length - 1
            ? $"{Quote(baseName[..dot])}.{Quote(baseName[(dot + 1)..])}"
            : Quote(baseName);
        return reference + arguments;
    }
}
