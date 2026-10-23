// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// The product a table-quench fixture deploys as. Removing what a product no longer declares (foreign keys, tables)
/// covers everything the product owns in the database, so two fixtures deploying as one product into the shared
/// database at the same time remove each other's objects. Each fixture is its own product.
/// </summary>
internal static class FixtureProduct
{
    // Stable across runs and well inside SQL Server's 50-character product name.
    public static string NameFor(Type fixture)
    {
        var hash = 2166136261u;
        foreach (var c in fixture.FullName ?? fixture.Name)
            hash = (hash ^ c) * 16777619u;
        return $"Quench Table Tests {hash:x8}";
    }
}
