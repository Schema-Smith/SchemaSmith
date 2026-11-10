// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Schema.Domain;

namespace Schema.Validation;

/// <summary>
/// Orchestrates a `--Validate` run: loads the package, and — if the load itself succeeds — runs
/// every registered <see cref="ISchemaCheck"/> and aggregates their findings. A load failure is
/// reported as a single finding (v1 LOAD-GATE) rather than propagating the exception: reporting
/// cleanly instead of crashing is the point of the linter.
/// </summary>
public sealed class SchemaPackageValidator
{
    private readonly Func<LoadedPackage> _loader;
    private readonly IReadOnlyList<ISchemaCheck> _checks;

    public SchemaPackageValidator(Func<LoadedPackage> loader, IReadOnlyList<ISchemaCheck> checks)
    {
        _loader = loader;
        _checks = checks;
    }

    public ValidationResult Validate(string packagePath)
    {
        LoadedPackage pkg;
        try
        {
            pkg = _loader();
        }
        catch (Exception e)
        {
            var location = e.Message.Split('\n')[0].TrimEnd('\r');
            return new ValidationResult(new[]
            {
                new Finding(Severity.Error, "SS-LOAD-001", "Load", location, e.Message)
            });
        }

        // Platform is what every downstream check keys off -- which JSON schema to validate against,
        // which dialect to parse identifiers in. Without it GetBasePlatform throws, and because checks
        // deliberately have no per-check try/catch (below) that surfaced as a raw stack trace instead of
        // a finding. Report it as one: the package cannot be meaningfully validated at all.
        if (pkg.Product == null || pkg.Product.Platform == Platform.Unknown)
            return new ValidationResult(new[]
            {
                new Finding(Severity.Error, "SS-LOAD-003", "Load", Path.Join(packagePath, "Product.json"),
                    "Product.json does not declare a Platform. Every check depends on the target engine "
                    + "(which schema to validate against, how to read identifiers), so nothing further can "
                    + "be checked. Add \"Platform\": \"SqlServer\" | \"PostgreSQL\" | \"MySQL\" | \"MariaDb\".")
            });

        var ctx = new ValidationContext(pkg.Product, pkg.Templates, packagePath);
        // No per-check try/catch (YAGNI) — the real checks don't throw; a check exception is a
        // bug in the check and should surface, not be swallowed as a finding.
        // pkg.LoadFindings carries SS-LOAD-001s for component files the loader skipped rather than
        // aborted on (see PackageLoader) — merged in so a skip during loading is reported exactly
        // like any other finding, instead of vanishing because the load itself didn't throw.
        var loadFindings = pkg.LoadFindings ?? Array.Empty<Finding>();
        var checkFindings = _checks.SelectMany(check => check.Run(ctx)).ToList();
        var named = checkFindings.Select(f => f.Location).ToHashSet(StringComparer.Ordinal);
        var unnamed = (pkg.UnlessNamedLoadFindings ?? Array.Empty<Finding>()).Where(f => !named.Contains(f.Location));
        return new ValidationResult(loadFindings.Concat(unnamed).Concat(checkFindings));
    }
}
