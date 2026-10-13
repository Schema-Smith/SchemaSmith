// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Schema.Isolators;

namespace Schema.Validation.Checks;

/// <summary>
/// Reports a package script that re-creates a MariaDB sequence with <c>CREATE OR REPLACE SEQUENCE</c>
/// (<c>SS-SEQ-002</c>, Warning). Object scripts run on every deploy, and replacing a sequence resets it to its START
/// value, so NEXTVAL hands out numbers it has already given. SchemaTongs extracts <c>CREATE SEQUENCE IF NOT EXISTS</c>;
/// packages extracted by earlier versions carry the replacing form. A migration script runs once, so it is not reported.
/// </summary>
public sealed class SequenceScriptCheck : ISchemaCheck
{
    private const string Code = "SS-SEQ-002";
    private const string Category = "Script";
    private static readonly Regex ReplacesASequence = new(@"\bCREATE\s+OR\s+REPLACE\s+SEQUENCE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IEnumerable<Finding> Run(ValidationContext ctx)
    {
        var dir = ProductDirectoryWrapper.GetFromFactory();
        var file = ProductFileWrapper.GetFromFactory();
        if (!dir.Exists(ctx.PackagePath)) return Enumerable.Empty<Finding>();

        return dir.GetFiles(ctx.PackagePath, "*.sql", SearchOption.AllDirectories)
            .Where(path => !IsUnder(path, "MigrationScripts") && !IsUnder(path, ".json-schemas"))
            .Where(path => file.Exists(path) && ReplacesASequence.IsMatch(file.ReadAllText(path)))
            .Select(path => new Finding(Severity.Warning, Code, Category, path,
                "This script runs on every deploy and re-creates a sequence with CREATE OR REPLACE SEQUENCE, which resets " +
                "it to its START value, so NEXTVAL returns numbers it has already given. Use CREATE SEQUENCE IF NOT EXISTS, " +
                "as SchemaTongs extracts it."))
            .ToList();
    }

    private static bool IsUnder(string path, string folder) =>
        path.Replace('\\', '/').Split('/').Contains(folder, System.StringComparer.OrdinalIgnoreCase);
}
