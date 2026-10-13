// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;

namespace SchemaQuench.UnitTests;

// VerboseLogging governs a user script's own notices, as it governs PRINT on SQL Server; SchemaSmith's own progress
// notices always log. Context strings are the ones PostgreSQL sends (verified on the server).
public class PostgreSqlNoticeLoggingTests
{
    private const string FromSchemaSmith = "PL/pgSQL function \"SchemaSmith\".\"ModifiedTableQuench\"(boolean) line 12 at RAISE";
    private const string FromUserDoBlock = "PL/pgSQL function inline_code_block line 1 at RAISE";
    private const string FromUserFunction = "PL/pgSQL function userfn() line 1 at RAISE";

    [Test]
    public void SchemaSmithsOwnNotice_LogsWithoutVerboseLogging() =>
        Assert.That(DatabaseQuench.ShouldLogPostgreSqlNotice("Add Missing Columns", FromSchemaSmith, false), Is.True);

    [TestCase(FromUserDoBlock)]
    [TestCase(FromUserFunction)]
    [TestCase(null)]
    public void AUserScriptsNotice_LogsOnlyWithVerboseLogging(string where)
    {
        Assert.Multiple(() =>
        {
            Assert.That(DatabaseQuench.ShouldLogPostgreSqlNotice("user note", where, false), Is.False);
            Assert.That(DatabaseQuench.ShouldLogPostgreSqlNotice("user note", where, true), Is.True);
        });
    }

    [Test]
    public void AUserFunctionInASchemaNamedLikeSchemaSmith_IsStillTheUsers() =>
        Assert.That(DatabaseQuench.ShouldLogPostgreSqlNotice("x", "PL/pgSQL function \"SchemaSmithApp\".f() line 1 at RAISE", false), Is.False);

    [TestCase("relation \"t\" does not exist, skipping")]
    [TestCase("relation \"t\" already exists, skipping")]
    public void IfExistsSkipNotices_NeverLog(string message) =>
        Assert.That(DatabaseQuench.ShouldLogPostgreSqlNotice(message, FromSchemaSmith, true), Is.False);
}
