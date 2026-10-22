// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

[Category("MySQL")]
[Category("Integration")]
[TestFixture]
public class OwnershipRefusalTests : OwnershipRefusalSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MySQL";
    protected override string Quote(string name) => $"`{name}`";
    protected override string TableSchema => MainDb;

    protected override void ClearOwnership(string first, string second) =>
        Exec($"DELETE FROM SchemaSmith_ProductOwnership WHERE ProductName IN ('{first}', '{second}')");

    // Functional key parts arrive in 8.0.13; a rename drops one ahead of the rename and rebuilds it over the new name.
    private bool SupportsFunctionalIndexes => new Version(((string)Scalar("SELECT VERSION()")).Split('-')[0]) >= new Version(8, 0, 13);

    protected override string ExpressionIndex(string column) =>
        SupportsFunctionalIndexes ? $$""", { "Name": "ix_lower", "IndexColumns": "(lower(`{{column}}`))" }""" : "";

    protected override long ExpressionIndexCount() => Count(
        $"SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = '{MainDb}' AND TABLE_NAME = 'ownership_refusal_probe' AND INDEX_NAME = 'ix_lower'");
}
