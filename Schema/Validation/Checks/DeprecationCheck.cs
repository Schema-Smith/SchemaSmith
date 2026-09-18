// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;

namespace Schema.Validation.Checks;

/// <summary>
/// Reports every deprecated alias the package still depends on (<c>SS-DEP-*</c>, Warning). Load migrates
/// each alias and the package deploys, so without this a package on its way to breaking when the alias is
/// retired validated as a clean PASS. The migrations record <see cref="Schema.Domain.DeprecationNotice"/>s
/// on the template; this check only reports them, so the alias rules live in exactly one place.
/// </summary>
public sealed class DeprecationCheck : ISchemaCheck
{
    private const string Category = "Deprecated";

    public IEnumerable<Finding> Run(ValidationContext ctx) =>
        ctx.Templates
            .SelectMany(t => t.DeprecationNotices)
            .Select(n => new Finding(Severity.Warning, n.Code, Category, n.Location, n.Message));
}
