// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class MySqlKeyAlgorithmTests
{
    // The texts are what SHOW CREATE TABLE printed on MySQL 8.0.46 and MariaDB 11.4.13.
    [TestCase("/*!50100 PARTITION BY KEY */ /*!50611 ALGORITHM = 1 */ /*!50100 (id)\nPARTITIONS 3 */", 1)]
    [TestCase(" PARTITION BY KEY ALGORITHM = 1 (`id`)\nPARTITIONS 3", 1)]
    [TestCase(" PARTITION BY LINEAR KEY ALGORITHM = 2 (`id`)\nPARTITIONS 3", 2)]
    // MariaDB 12.3 names the algorithm.
    [TestCase(" PARTITION BY KEY ALGORITHM = MYSQL51 (`id`)\nPARTITIONS 2", 1)]
    [TestCase(" PARTITION BY KEY ALGORITHM = MYSQL55 (`id`)\nPARTITIONS 2", 2)]
    [TestCase("/*!50100 PARTITION BY KEY (id)\nPARTITIONS 2 */", null)]
    [TestCase(" PARTITION BY HASH (`id`)\nPARTITIONS 2", null)]
    [TestCase(null, null)]
    public void ReadsTheAlgorithmWhereTheEnginePrintsIt(string createTable, int? expected) =>
        Assert.That(MySqlKeyAlgorithm.Read(createTable), Is.EqualTo(expected));
}
