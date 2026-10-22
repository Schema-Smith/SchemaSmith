// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaTongs.IntegrationTests.Shared;

namespace SchemaTongs.IntegrationTests.MySQL;

[Category("MySQL")]
[TestFixture]
[Category("Integration")]
public class PartitionExtractionTests : PartitionExtractionSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string ConfigPrefix => "MySQL";
}
