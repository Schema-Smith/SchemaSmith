// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using Schema.Domain.MySQL;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

/// <summary>MY-A2: a FULLTEXT parser is read from SHOW CREATE TABLE, the only place either engine reports it.</summary>
public class MySqlFullTextParsersTests
{
    // Copied from MySQL 8.0's SHOW CREATE TABLE for the same table.
    private const string MySqlCreateTable = """
        CREATE TABLE `t` (
          `id` int NOT NULL,
          `a` text,
          `b c` text,
          PRIMARY KEY (`id`),
          FULLTEXT KEY `ft_a` (`a`) /*!50100 WITH PARSER `ngram` */ ,
          FULLTEXT KEY `ft b` (`a`,`b c`) COMMENT 'x',
          FULLTEXT KEY `ft_plain` (`a`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
        """;

    [Test]
    public void ReadsTheParserOfEachFullTextIndexThatHasOne()
    {
        var parsers = MySqlFullTextParsers.Read(MySqlCreateTable);
        Assert.That(parsers, Is.EquivalentTo(new Dictionary<string, string> { ["ft_a"] = "ngram" }));
    }

    [Test]
    public void ReadsTheBareFormAndEscapedNames()
    {
        var parsers = MySqlFullTextParsers.Read("  FULLTEXT INDEX `odd``name` (`a`) WITH PARSER my_parser,\n");
        Assert.That(parsers["odd`name"], Is.EqualTo("my_parser"));
    }

    [Test]
    public void SetsTheParserOnTheMatchingIndex_AndLeavesTheOthersAlone()
    {
        var table = new MySqlTable
        {
            FullTextIndexes =
            [
                new FullTextIndex { Name = "`FT_A`", Columns = "a" },
                new FullTextIndex { Name = "ft_plain", Columns = "a" },
                new FullTextIndex { Name = "ft b", Columns = "a,b c", Parser = "declared" }
            ]
        };
        MySqlFullTextParsers.Apply(table, MySqlCreateTable);
        Assert.That(table.FullTextIndexes.Select(i => i.Parser), Is.EqualTo(new[] { "ngram", null, "declared" }));
    }

    [Test]
    public void NoTextMeansNoParsers()
    {
        Assert.That(MySqlFullTextParsers.Read(null), Is.Empty);
    }
}
