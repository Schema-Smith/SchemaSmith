// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Schema.Domain;
using Schema.Utility;

namespace Schema.Validation;

/// <summary>
/// Thin production loader for `--Validate`: reads the configured package from disk. Mirrors
/// <c>ProductQuench.LoadTemplates</c> minus the deploy-only special-token cross-resolution (the
/// linter only needs the loaded domain objects, not deployment-time token wiring). Integration-
/// covered in Slice 3 — not unit-tested here (would require a real package on disk).
/// <para>
/// Loads leniently on purpose — this is the one caller that passes
/// <see cref="MissingMemberHandling.Ignore"/> to BOTH <see cref="Product.Load"/> and
/// <see cref="Template.Load"/>, plus <c>tolerateComponentLoadErrors: true</c> and
/// <c>tolerateFileTokenErrors: true</c> to <see cref="Template.Load"/>. `--Validate`'s contract
/// (<c>SchemaPackageValidator</c>) is to enumerate every problem it can find in one pass; strict
/// deserialization aborts the whole run on the FIRST unrecognised property, hiding every other
/// finding a full pass would otherwise report. A misnamed property in either manifest is tolerated
/// at load time and reported precisely as SS-JSON-001 by <c>JsonSchemaCheck</c>'s independent
/// re-validation of the raw file, rather than aborting the load over it. A component file that
/// still fails to load outright (malformed JSON, not just an unknown property) is skipped the same
/// way. An unresolvable <c>&lt;*File*&gt;</c> ScriptTokens entry is likewise captured rather than
/// thrown, and turned into an SS-TOK-004 finding below — that gap used to vanish silently (neither
/// reported nor failing the run) because <see cref="TryLoadTemplate"/>'s catch used to discard the
/// exception <see cref="Schema.Utility.TokenHelper.ResolveFileTokens"/> raised unconditionally. A
/// template whose OWN manifest can't be loaded AT ALL (genuinely unparseable, or a
/// self-contradictory configuration) is the same failure shape one level up — see
/// <see cref="TryLoadTemplate"/> for how that's reported as SS-LOAD-002 instead of silently
/// excluding the template. The deploy path (<c>ProductQuench</c>) never sets any of these flags,
/// so an unrecognised property, a load failure, or an unresolvable file token there still aborts
/// the run as intended.
/// </para>
/// </summary>
public static class PackageLoader
{
    private const string FileTokenCode = "SS-TOK-004";
    private const string TokenCategory = "Token";
    private const string TemplateLoadFailureCode = "SS-LOAD-002";

    public static LoadedPackage LoadPackage()
    {
        // tolerateFileTokenErrors: true — an unresolvable <*File*> token must not silently vanish
        // (either by aborting the whole load, or via TryLoadTemplate's catch below): it is
        // recorded on Product.FileTokenErrors instead of thrown, same mechanism as the template
        // case just below.
        var product = Product.Load(MissingMemberHandling.Ignore, tolerateFileTokenErrors: true);

        var templates = new List<Template>();
        var templateLoadFindings = new List<Finding>();
        foreach (var name in product.TemplateOrder.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var (template, failure) = TryLoadTemplate(name, product);
            if (template != null) templates.Add(template);
            if (failure != null) templateLoadFindings.Add(failure);
        }

        // Skip-and-report, not skip-and-forget: a component file Template.Load excluded because it
        // wasn't valid JSON at all still needs to surface as a finding, or the run would exit clean
        // over a package it silently loaded less of. ComponentLoadErrors records EVERY component that
        // failed to load — consumers listing the bad files depend on that — so the filter lives here,
        // where the question is what `--Validate` should PRINT: a parseable-but-wrong component is
        // dropped because JsonSchemaCheck's own pass reports it precisely as SS-JSON-001, and emitting
        // both would report one file twice.
        var loadFindings = templates
            .SelectMany(t => t.ComponentLoadErrors)
            .Where(e => !e.IsValidJson)
            .Select(e => new Finding(Severity.Error, "SS-LOAD-001", "Load", e.FilePath, e.Message))
            // A template whose own Template.json couldn't be loaded at all — see TryLoadTemplate.
            .Concat(templateLoadFindings)
            // Same skip-and-report duty for file-token resolution failures (product- and
            // template-level ScriptTokens both funnel here) — see TokenHelper.ResolveFileTokens's
            // tolerateErrors parameter. Without this, a defined-but-unresolvable <*File*> token
            // would satisfy TokenCheck's SS-TOK-001 (it only checks the key exists) and still pass
            // clean, even though the same package fails at deploy.
            .Concat(product.FileTokenErrors.Select(e => new Finding(Severity.Error, FileTokenCode, TokenCategory, e.FilePath, e.Message)))
            .Concat(templates.SelectMany(t => t.FileTokenErrors)
                .Select(e => new Finding(Severity.Error, FileTokenCode, TokenCategory, e.FilePath, e.Message)))
            .ToList();
        return new LoadedPackage(product, templates, loadFindings);
    }

    // A template whose own Template.json can't even be loaded — genuinely unparseable JSON, or a
    // self-contradictory configuration ValidateSchemaTemplateRules rejects — is excluded from
    // `templates` rather than aborting LoadPackage; every OTHER template still gets full check
    // coverage. missingMemberHandling: Ignore (mirroring Product.Load) means a merely misnamed
    // property does NOT land here at all: it loads successfully, the template gets full check
    // coverage, and JsonSchemaCheck's independent pass reports it precisely as SS-JSON-001 — one
    // useful finding instead of an excluded template plus a vague SS-LOAD-002. That handoff is
    // sound only because JsonSchemaCheck now generates its comparison schema in memory when none
    // is committed (JsonSchemaCheck.Run) — before that change it skipped silently on a package
    // with no committed .json-schemas/, which is why this method used to report SS-LOAD-002
    // unconditionally for that case too. If schema generation ever goes back to being conditional,
    // this handoff needs revisiting.
    //
    // The catch is narrowed to the two exception types a package-content problem can still raise
    // anywhere inside Template.Load: JsonException (JsonHelper.TemplateLoad — won't parse at all)
    // and InvalidOperationException (a ValidateSchemaTemplateRules rule violation, or a token-graph
    // cycle ResolveTokenScopes detects only after every component has already loaded). Anything
    // else is a genuine bug, not a package-content problem, and must surface.
    private static (Template Template, Finding Failure) TryLoadTemplate(string name, Product product)
    {
        try
        {
            return (Template.Load(name, product, tolerateComponentLoadErrors: true, tolerateFileTokenErrors: true,
                missingMemberHandling: MissingMemberHandling.Ignore), null);
        }
        catch (JsonException e)
        {
            return (null, BuildTemplateLoadFailure(name, product, e));
        }
        catch (InvalidOperationException e)
        {
            return (null, BuildTemplateLoadFailure(name, product, e));
        }
        // A TemplateOrder entry that names no template folder, or a Template.json that is missing: a package
        // problem the deploy refuses, so --Validate reports it rather than stopping.
        catch (RunFailedException e)
        {
            return (null, BuildTemplateLoadFailure(name, product, e));
        }
    }

    private static Finding BuildTemplateLoadFailure(string name, Product product, Exception e)
    {
        var templateFilePath = Template.GetTemplateFilePath(product, name);
        return new Finding(Severity.Error, TemplateLoadFailureCode, "Load", templateFilePath,
            $"Template '{name}' could not be loaded — every check for this template is skipped: {e.Message}");
    }
}
