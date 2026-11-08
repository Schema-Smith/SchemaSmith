// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class DatabaseSettingsTests : DatabaseSettingsSharedTests
{
    private string _original;

    protected override Platform Platform => Platform.MariaDb;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MariaDB";

    protected override string Kind => "Option";
    protected override string Name => "COLLATE";
    protected override string ValueA => "utf8mb4_general_ci";
    protected override string ValueB => "utf8mb4_bin";
    protected override string EquivalentSpellingOfA => "UTF8MB4_GENERAL_CI";
    protected override (string Name, string Value, string Action) Refused => ("COLLATE", "no_such_collation_ci", "fixFailed");

    [OneTimeSetUp]
    public void RememberTheCollation() => _original = ReadCurrent();

    protected override string ReadCurrent() =>
        Scalar($"SELECT DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = '{MainDb}'")?.ToString();

    protected override void SetCurrent(string value) => Exec($"ALTER DATABASE `{MainDb}` COLLATE {value}");
    protected override void Restore() => SetCurrent(_original ?? "utf8mb4_general_ci");
}
