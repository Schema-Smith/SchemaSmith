// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using NUnit.Framework;
using Schema.Domain;
using Schema.Utility;
using Schema.Validation.Checks;

namespace Schema.UnitTests.Validation.Checks;

/// <summary>
/// <see cref="JsonSchemaCheck"/> carries a private copy of RepositoryHelper's schema-file → domain-type
/// mapping, duplicated because SchemaQuench must not reach into Schema's private members. The two drifting
/// out of lockstep is not hypothetical: the F4 (events) and F5 (enum types / sequences) promotions each
/// added a schema file to <see cref="RepositoryHelper.GetSchemaFileNames"/> and did NOT add the matching
/// row here, so <c>--Validate</c> threw <c>Unknown schema file mapping</c> on every clean package for those
/// engines — and the full integration gate was the only thing that caught it.
///
/// This guard makes the outcome the assertion: every schema file the repository will ever ask the validator
/// to resolve must resolve. Add a declarative type without wiring the validator and this reddens at unit
/// speed, instead of an hour into the integration suite.
/// </summary>
[TestFixture]
public class SchemaFileMappingParityTests
{
    private static readonly Platform[] Platforms =
        { Platform.SqlServer, Platform.PostgreSQL, Platform.MySQL, Platform.MariaDb };

    [Test]
    public void EverySchemaFileTheRepositoryProduces_ResolvesInTheValidator()
    {
        foreach (var platform in Platforms)
        {
            foreach (var fileName in RepositoryHelper.GetSchemaFileNames(platform))
            {
                Assert.DoesNotThrow(
                    () => JsonSchemaCheck.GetTypeForSchemaFile(fileName, platform),
                    $"RepositoryHelper produces '{fileName}' for {platform}, but JsonSchemaCheck cannot map "
                    + "it -- the two mappings have drifted. Add the matching row to "
                    + "JsonSchemaCheck.GetTypeForSchemaFile.");
            }
        }
    }

    /// <summary>
    /// Resolving *something* is not parity. The validator regenerates a schema from the type it picks and
    /// DeepEquals it against the committed file, which the repository generated from ITS type -- so the two
    /// must be the SAME type, not merely both resolvable. Asserting no-throw let MariaDB tables drift:
    /// JsonSchemaCheck folded MariaDb to its MySQL base and returned MySqlTable, which throws nothing but
    /// omits every MariaDbTable-only property (IsSystemVersioned, Periods, Encrypted, PageCompressed...),
    /// so every MariaDB package reported a permanent false SS-STALE-001 while its committed schema was
    /// perfectly current. Assert the outcome that actually matters.
    /// </summary>
    [Test]
    public void EverySchemaFile_ResolvesToTheSameTypeInBothMappings()
    {
        foreach (var platform in Platforms)
        {
            foreach (var fileName in RepositoryHelper.GetSchemaFileNames(platform))
            {
                Assert.That(JsonSchemaCheck.GetTypeForSchemaFile(fileName, platform),
                    Is.EqualTo(RepositoryHelper.GetTypeForSchemaFile(fileName, platform)),
                    $"'{fileName}' for {platform} resolves to a DIFFERENT domain type in JsonSchemaCheck than "
                    + "in RepositoryHelper. The validator would compare the committed schema against one "
                    + "generated from the wrong type and report a false SS-STALE-001 forever.");
            }
        }
    }
}
