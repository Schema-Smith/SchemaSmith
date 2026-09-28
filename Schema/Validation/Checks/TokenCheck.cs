// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.Validation.Checks;

/// <summary>
/// Text-level <c>{{Token}}</c> lint across the RAW package files, not the loaded domain model.
/// <c>Template.Load</c> resolves known tokens IN PLACE during load (<see cref="Schema.Domain.Table.
/// ResolveScriptTokensInTableComponentScripts"/> and friends), so a post-load scan of the domain
/// model can no longer distinguish a token definition from a reference — only the raw files on
/// disk still show both sides. This check therefore reads <see cref="ValidationContext.PackagePath"/>
/// as text via the same Product File/Directory isolators <c>Template.Load</c> itself uses,
/// never the loaded <c>Product</c>/<c>Template</c> objects.
/// <para>
/// SAFETY VALVE (critical for trust): the DEFINED-token set is built to be over-inclusive rather
/// than exact. Built-ins, every <c>ScriptTokens</c> key found in any <c>Product.json</c>/
/// <c>Template.json</c>, and every Extensions-derived name (in ALL of its bare/"Table."/
/// "MaterializedView."/"IndexedView."-prefixed forms — <see cref="Schema.Domain.Table.
/// GetCustomTokens"/>'s exact prefix depends on which object owns the Extensions block, which
/// this raw-text scan cannot always re-derive) all count as defined. A linter that flags a
/// legitimately-defined token is worse than one that misses a few, so uncertainty always resolves
/// to "assume defined" here — false negatives are the deliberately chosen failure mode.
/// </para>
/// </summary>
public sealed class TokenCheck : ISchemaCheck
{
    private const string UndefinedCode = "SS-TOK-001";
    private const string MalformedCode = "SS-TOK-002";
    private const string UnusedCode = "SS-TOK-003";
    private const string Category = "Token";

    // Built-in / context tokens are always present after Template.Load regardless of what
    // Product.json / Template.json declare — confirmed from source, not guessed:
    //  - ProductName            Schema/Domain/Product.cs:            product.ScriptTokens.Add("ProductName", product.Name);
    //  - TemplateName           Schema/Domain/Template.cs:            tokens.Add(new("TemplateName", Name ?? "UNSPECIFIED"));
    //  - TableSchema            Schema/Domain/Template.cs:            tokens.Add(new("TableSchema", TableSchema...));
    //  - MaterializedViewSchema Schema/Domain/Template.cs:            tokens.Add(new("MaterializedViewSchema", ...));
    //  - IndexedViewSchema      Schema/Domain/Template.cs:            tokens.Add(new("IndexedViewSchema", ...));
    //  - SchemaName             Schema/Domain/SchemaDefaultResolver.cs: SchemaNameToken = "{{SchemaName}}"
    //                           (substituted per-iteration during schema-template fan-out)
    //  - repo_path              Schema/Domain/Product.cs:35: BranchNameFile defaults to
    //                           "{{repo_path}}/.git/HEAD"
    //  - BranchName             reserved token resolved outside the CLI's static view
    //                           (branch-name derivation), alongside repo_path above
    //  - ServerMajorVersion     SchemaQuench/DatabaseQuench.cs: PrepareVersionScriptTokens (A1) — the
    //                           detected target server major version, resolved per-target in Phase B
    //  - CompatibilityLevel     SchemaQuench/DatabaseQuench.cs: PrepareVersionScriptTokens (A1) — the
    //                           SQL Server database compatibility level (falls back to the server
    //                           version off SQL Server), resolved per-target in Phase B
    //  - TableXml / MaterializedViewXml / IndexedViewXml   Schema/Domain/Template.cs: XML twins of the
    //                           *Schema tokens (A2), for shredding the model as XML on the legacy SQL Server tier
    private static readonly HashSet<string> BuiltInTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ProductName", "TemplateName", "SchemaName", "TableSchema", "MaterializedViewSchema", "IndexedViewSchema",
        "TableXml", "MaterializedViewXml", "IndexedViewXml",
        "repo_path", "BranchName", "ServerMajorVersion", "CompatibilityLevel"
    };

    // Table.GetCustomTokens(Extensions, baseName) prefixes an Extensions block's names with
    // "" (a table's own ShouldApplyExpression), "Table." (table-owned Extensions reachable from
    // child column/check/fk/index scripts), "MaterializedView.", or "IndexedView." depending on
    // which object owns the block — a raw-text scan can't always tell which, so every prefix is
    // accepted for every Extensions-derived name (safety valve).
    private static readonly string[] ExtensionsPrefixes = { "", "Table.", "MaterializedView.", "IndexedView." };

    // A well-formed "{{...}}" whose contents aren't a valid token identifier isn't a token
    // reference at all — e.g. Postgres's 2-D text-array literal syntax
    // '{{ns,http://schemas.example.com/ns}}'::text[] contains commas/colons/slashes that no
    // real token name would ever have. Dots are allowed for the Extensions "Table."/
    // "MaterializedView."/"IndexedView." prefixed forms. Only applies to undefined-token
    // detection (SS-TOK-001) — a genuinely unmatched "{{" is still flagged by SS-TOK-002
    // regardless of what's inside it.
    private static readonly Regex TokenIdentifierPattern = new(@"^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.Compiled);

    public IEnumerable<Finding> Run(ValidationContext ctx)
    {
        var dir = ProductDirectoryWrapper.GetFromFactory();
        var file = ProductFileWrapper.GetFromFactory();

        if (!dir.Exists(ctx.PackagePath)) return Enumerable.Empty<Finding>();

        var jsonFiles = dir.GetFiles(ctx.PackagePath, "*.json", SearchOption.AllDirectories)
            .Where(f => !IsUnderJsonSchemasDir(f))
            .ToList();
        var sqlFiles = dir.GetFiles(ctx.PackagePath, "*.sql", SearchOption.AllDirectories)
            .Where(f => !IsUnderJsonSchemasDir(f))
            .ToList();

        var defined = new HashSet<string>(BuiltInTokens, StringComparer.OrdinalIgnoreCase);
        // First occurrence wins — good enough for a "does this exist anywhere" unused check;
        // the location just needs to point somewhere useful, not enumerate every definition site.
        var scriptTokenSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var jsonFile in jsonFiles)
        {
            var text = SafeReadText(file, jsonFile);
            if (text == null) continue;

            var root = SafeParseJObject(text);
            if (root == null) continue;

            var fileName = Path.GetFileName(jsonFile);
            var isProductOrTemplate =
                !IsUnderComponentFolder(jsonFile) &&
                (fileName.Equals("Product.json", StringComparison.OrdinalIgnoreCase) ||
                 fileName.Equals("Template.json", StringComparison.OrdinalIgnoreCase));

            if (isProductOrTemplate && root["ScriptTokens"] is JObject scriptTokens)
            {
                foreach (var prop in scriptTokens.Properties())
                {
                    defined.Add(prop.Name);
                    if (!scriptTokenSources.ContainsKey(prop.Name))
                        scriptTokenSources[prop.Name] = jsonFile;
                }
            }

            foreach (var extName in FindExtensionsTokenNames(root))
            foreach (var prefix in ExtensionsPrefixes)
                defined.Add(prefix + extName);
        }

        var findings = new List<Finding>();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flaggedUndefined = new HashSet<(string File, string Token)>();

        foreach (var scannedFile in jsonFiles.Concat(sqlFiles))
        {
            var text = SafeReadText(file, scannedFile);
            if (text == null) continue;

            if (HasUnmatchedOpenBrace(text))
                findings.Add(new Finding(Severity.Error, MalformedCode, Category, scannedFile,
                    $"Contains an unmatched '{{{{' with no closing '}}}}'."));

            foreach (var token in TokenHelper.GetTokensFromString(text))
            {
                // A REFERENCE is whatever the resolver would substitute, so the reference set uses the
                // resolver's own rule and nothing narrower. Filtering it by TokenIdentifierPattern reported a
                // token named "My Token" as never referenced (SS-TOK-003) while the deploy substituted it.
                referenced.Add(token);
                if (defined.Contains(token)) continue;
                // The identifier filter belongs HERE and only here: it keeps text that merely looks like a
                // token -- a PostgreSQL array literal '{{ns,http://...}}' -- out of UNDEFINED detection.
                if (!TokenIdentifierPattern.IsMatch(token)) continue;
                if (!flaggedUndefined.Add((scannedFile, token))) continue;
                findings.Add(new Finding(Severity.Error, UndefinedCode, Category, scannedFile,
                    $"References undefined token '{{{{{token}}}}}'."));
            }
        }

        foreach (var (name, sourceFile) in scriptTokenSources)
        {
            if (referenced.Contains(name)) continue;
            findings.Add(new Finding(Severity.Warning, UnusedCode, Category, sourceFile,
                $"ScriptTokens entry '{name}' (defined in {sourceFile}) is never referenced anywhere in the package."));
        }

        return findings;
    }

    private static string SafeReadText(IFile file, string path)
    {
        try
        {
            return file.Exists(path) ? file.ReadAllText(path) : null;
        }
        catch
        {
            // Unreadable file — safety valve applies here too: skip rather than crash the whole
            // `--Validate` run over one bad file.
            return null;
        }
    }

    private static JObject SafeParseJObject(string text)
    {
        try
        {
            return JObject.Parse(text);
        }
        catch
        {
            // Malformed JSON is not this check's concern (JSON-schema validation / package load
            // already surfaces that) — skip rather than guess at partial content.
            return null;
        }
    }

    private static bool IsUnderJsonSchemasDir(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals(".json-schemas", StringComparison.OrdinalIgnoreCase));

    // A file directly under a component folder (Tables/Indexed Views/Materialized Views) is
    // never the product/template manifest, even if it happens to be named Product.json/
    // Template.json (e.g. MySQL's schema-less <table>.json layout for a table named "Product").
    // Mirrors JsonSchemaCheck.MapFileToType's directory-context precedence.
    private static bool IsUnderComponentFolder(string path)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        return parent.Equals("Tables", StringComparison.OrdinalIgnoreCase) ||
               parent.Equals("Indexed Views", StringComparison.OrdinalIgnoreCase) ||
               parent.Equals("Materialized Views", StringComparison.OrdinalIgnoreCase);
    }

    // Mirrors TokenHelper.GetTokensFromString's own "{{" / "}}" scan, but flips the "no closing
    // found" case (which GetTokensFromString silently treats as end-of-tokens) into a positive
    // malformed signal. One finding per file regardless of how many unmatched braces it contains
    // — enough to point the user at the file without flooding them with duplicate findings.
    private static bool HasUnmatchedOpenBrace(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var pos = 0;
        while (true)
        {
            var open = text.IndexOf("{{", pos, StringComparison.Ordinal);
            if (open < 0) return false;
            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) return true;
            pos = close + 2;
        }
    }

    // Walks the whole JSON document looking for any "Extensions" object property at any depth
    // (table-level, column-level, index-level, etc.) and collects the leaf names it would produce
    // as tokens — mirrors Table.GetCustomTokens/ProcessJObject's own recursive dotted-name
    // construction, but only needs the resulting KEY names, not resolved values.
    private static IEnumerable<string> FindExtensionsTokenNames(JToken node)
    {
        switch (node)
        {
            case JObject obj:
                foreach (var prop in obj.Properties())
                {
                    if (prop.Name == "Extensions" && prop.Value is JObject extObj)
                    {
                        foreach (var name in CollectNames(extObj, ""))
                            yield return name;
                    }
                    else
                    {
                        foreach (var name in FindExtensionsTokenNames(prop.Value))
                            yield return name;
                    }
                }
                break;
            case JArray arr:
                foreach (var item in arr)
                foreach (var name in FindExtensionsTokenNames(item))
                    yield return name;
                break;
        }
    }

    private static IEnumerable<string> CollectNames(JObject obj, string baseName)
    {
        foreach (var prop in obj.Properties())
        {
            if (prop.Value is JObject nested)
            {
                foreach (var name in CollectNames(nested, $"{baseName}{prop.Name}."))
                    yield return name;
            }
            else
            {
                yield return $"{baseName}{prop.Name}";
            }
        }
    }
}
