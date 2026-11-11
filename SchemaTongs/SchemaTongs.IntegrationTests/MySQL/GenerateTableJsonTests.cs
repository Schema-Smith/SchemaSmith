// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaTongs.IntegrationTests.Shared;

namespace SchemaTongs.IntegrationTests.MySQL;

[Category("MySQL")]
[TestFixture]
[Category("Integration")]
public class GenerateTableJsonTests : GenerateTableJsonSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string ConfigPrefix => "MySQL";

    // MySQL 8.0.19+ drops integer display widths (`int`); 5.7 and 8.0 before 8.0.19 keep them (`int(11)`), as MariaDB
    // does. 8.0 reports the default FK action as `NO ACTION` where 5.7 reports `RESTRICT`.
    protected override string ExpectedIntegerType(string canonical) => (ServerPatchNum ?? 80019) >= 80019
        ? canonical
        : canonical switch
        {
            "tinyint" => "tinyint(4)",
            "smallint" => "smallint(6)",
            "mediumint" => "mediumint(9)",
            "int" => "int(11)",
            "bigint" => "bigint(20)",
            _ => canonical
        };

    protected override string ExpectedDefaultFkAction => ServerVersionNum >= 800 ? "NO ACTION" : "RESTRICT";
}
