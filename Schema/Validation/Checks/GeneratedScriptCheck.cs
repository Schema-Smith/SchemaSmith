// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.Validation.Checks;

/// <summary>
/// Reports a script SchemaTongs generated that has since been edited by hand (<c>SS-GEN-001</c>, Warning). SchemaTongs
/// writes such a script in full on every extraction, a prerequisite script for one, so the edit is lost the next time
/// the package is extracted. A script without the generated marker is the user's own and is not checked.
/// </summary>
public sealed class GeneratedScriptCheck : ISchemaCheck
{
    private const string Code = "SS-GEN-001";
    private const string Category = "Script";

    public IEnumerable<Finding> Run(ValidationContext ctx)
    {
        var dir = ProductDirectoryWrapper.GetFromFactory();
        var file = ProductFileWrapper.GetFromFactory();
        if (!dir.Exists(ctx.PackagePath)) return Enumerable.Empty<Finding>();

        return dir.GetFiles(ctx.PackagePath, "*.sql", SearchOption.AllDirectories)
            .Where(path => file.Exists(path) && GeneratedScript.IsHandEdited(file.ReadAllText(path)))
            .Select(path => new Finding(Severity.Warning, Code, Category, path,
                "SchemaTongs generated this script and it has been edited since. The next extraction overwrites it, so " +
                "make the change in the source database instead, or remove the first line to make the script your own."))
            .ToList();
    }
}
