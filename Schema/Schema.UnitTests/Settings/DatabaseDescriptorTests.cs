// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using NSubstitute;
using Schema.Domain;
using Schema.Domain.Settings;
using Schema.Settings;

namespace Schema.UnitTests.Settings;

[TestFixture]
public class DatabaseDescriptorTests
{
    private static (IDbCommand Cmd, List<string> Sql, Dictionary<string, object> Params) Recorder()
    {
        var sql = new List<string>();
        var parms = new Dictionary<string, object>();
        var cmd = Substitute.For<IDbCommand>();
        var collection = Substitute.For<IDataParameterCollection>();
        cmd.Parameters.Returns(collection);
        cmd.CreateParameter().Returns(_ =>
        {
            var p = Substitute.For<IDbDataParameter>();
            p.When(x => x.Value = Arg.Any<object>()).Do(ci => parms[p.ParameterName] = ci.Arg<object>());
            return p;
        });
        cmd.When(c => c.ExecuteNonQuery()).Do(_ => sql.Add(cmd.CommandText));
        return (cmd, sql, parms);
    }

    [TestCase(Platform.SqlServer, "Option", typeof(SqlServerOptionDescriptor))]
    [TestCase(Platform.SqlServer, "ScopedConfiguration", typeof(SqlServerScopedConfigurationDescriptor))]
    [TestCase(Platform.PostgreSQL, "Parameter", typeof(PostgreSqlDatabaseParameterDescriptor))]
    [TestCase(Platform.MySQL, "Option", typeof(MySqlOptionDescriptor))]
    [TestCase(Platform.MariaDb, "option", typeof(MySqlOptionDescriptor))]
    public void Factory_PicksTheDescriptorForTheKind(Platform platform, string kind, Type expected)
    {
        Assert.That(SettingDescriptors.For(platform, SettingScope.Database, kind, 16), Is.InstanceOf(expected));
    }

    [Test]
    public void Factory_UnknownKind_Throws()
    {
        Assert.That(() => SettingDescriptors.For(Platform.PostgreSQL, SettingScope.Database, "Option", 16), Throws.ArgumentException);
    }

    [Test]
    public void SqlServerOption_SetsTheKeyword_OnTheQuotedDatabase()
    {
        var (cmd, sql, _) = Recorder();
        new SqlServerOptionDescriptor().Apply(cmd, "auto_update_statistics_async", "ON", "my]db");
        Assert.That(sql, Is.EqualTo(new[] { "ALTER DATABASE [my]]db] SET AUTO_UPDATE_STATISTICS_ASYNC ON" }));
    }

    [TestCase("COMPATIBILITY_LEVEL", "150", "ALTER DATABASE [d] SET COMPATIBILITY_LEVEL = 150")]
    [TestCase("TARGET_RECOVERY_TIME", "60", "ALTER DATABASE [d] SET TARGET_RECOVERY_TIME = 60 SECONDS")]
    [TestCase("TARGET_RECOVERY_TIME", "60 seconds", "ALTER DATABASE [d] SET TARGET_RECOVERY_TIME = 60 SECONDS")]
    [TestCase("DELAYED_DURABILITY", "allowed", "ALTER DATABASE [d] SET DELAYED_DURABILITY = ALLOWED")]
    [TestCase("RECOVERY", "simple", "ALTER DATABASE [d] SET RECOVERY SIMPLE")]
    public void SqlServerOption_Syntax(string name, string value, string expected)
    {
        var (cmd, sql, _) = Recorder();
        new SqlServerOptionDescriptor().Apply(cmd, name, value, "d");
        Assert.That(sql, Is.EqualTo(new[] { expected }));
    }

    // ALTER DATABASE takes keywords and numbers, not literals, so a value that is not one is refused rather than spliced.
    [TestCase("O'Brien; DROP TABLE x")]
    [TestCase("ON; SHUTDOWN")]
    [TestCase("ON --")]
    public void SqlServerOption_RefusesAValueThatIsNotAKeywordOrNumber(string value)
    {
        var (cmd, sql, _) = Recorder();
        Assert.That(() => new SqlServerOptionDescriptor().Apply(cmd, "AUTO_CLOSE", value, "d"),
            Throws.InstanceOf<SettingValueException>().With.Message.Contains("not a keyword or number"));
        Assert.That(sql, Is.Empty);
    }

    [Test]
    public void SqlServerOption_AnOptionOutsideTheMap_IsNotReadAndNotFixable()
    {
        var reading = new SqlServerOptionDescriptor().Read(Substitute.For<IDbCommand>(), "CURSOR_DEFAULT", "d");
        Assert.That(reading.Exists, Is.False);
        Assert.That(reading.UnfixableReason, Does.Contain("not a database option SchemaSmith reads"));
    }

    [Test]
    public void SqlServerScopedConfiguration_SetsInTheCurrentDatabase()
    {
        var (cmd, sql, _) = Recorder();
        new SqlServerScopedConfigurationDescriptor(16).Apply(cmd, "MAXDOP", "4", "d");
        Assert.That(sql, Is.EqualTo(new[] { "ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP = 4" }), "the option is a keyword, not a bracketed name");
    }

    [Test]
    public void SqlServerScopedConfiguration_AnOptionThatIsNotAKeyword_IsRefused()
    {
        var (cmd, sql, _) = Recorder();
        Assert.That(() => new SqlServerScopedConfigurationDescriptor(16).Apply(cmd, "MAXDOP] = 1; DROP", "4", "d"), Throws.InstanceOf<SettingValueException>());
        Assert.That(sql, Is.Empty);
    }

    // Snapshot options need the database to themselves; on a busy database the change fails at once rather than queueing
    // every new connection behind it until the command times out.
    [TestCase("READ_COMMITTED_SNAPSHOT", "ALTER DATABASE [d] SET READ_COMMITTED_SNAPSHOT ON WITH NO_WAIT")]
    [TestCase("ALLOW_SNAPSHOT_ISOLATION", "ALTER DATABASE [d] SET ALLOW_SNAPSHOT_ISOLATION ON WITH NO_WAIT")]
    public void SqlServerOption_SnapshotOptions_DoNotWait(string name, string expected)
    {
        var (cmd, sql, _) = Recorder();
        new SqlServerOptionDescriptor().Apply(cmd, name, "ON", "d");
        Assert.That(sql, Is.EqualTo(new[] { expected }));
    }

    // search_path and the preload lists quote a single literal as one element: 'a, b' would become the one name "a, b".
    [TestCase("search_path", "app, public", "ALTER DATABASE \"d\" SET \"search_path\" = 'app', 'public'")]
    [TestCase("work_mem", "8MB", "ALTER DATABASE \"d\" SET \"work_mem\" = '8MB'")]
    public void PostgreSqlParameter_AListIsSentElementByElement(string name, string value, string expected)
    {
        var (cmd, sql, _) = Recorder();
        new PostgreSqlDatabaseParameterDescriptor().Apply(cmd, name, value, "d");
        Assert.That(sql, Is.EqualTo(new[] { expected }));
    }

    [TestCase("app, public", "app,public", true)]
    [TestCase("app,public", "\"app\", public", true)]
    [TestCase("app, public", "public, app", false)]
    public void PostgreSqlParameter_AListComparesElementByElement(string declared, string current, bool equal)
    {
        var d = new PostgreSqlDatabaseParameterDescriptor();
        Assert.That(d.EquivalentFor("search_path", declared, new SettingReading(true, current, current, false, null)), Is.EqualTo(equal));
    }

    [TestCase("utf8_general_ci", "utf8mb3_general_ci", true)]
    [TestCase("utf8", "utf8mb3", true)]
    [TestCase("utf8mb4_bin", "utf8mb4_general_ci", false)]
    public void MySqlOption_Utf8IsUtf8mb3(string declared, string current, bool equal)
    {
        Assert.That(new MySqlOptionDescriptor().Equivalent(declared, new SettingReading(true, current, current, false, null)), Is.EqualTo(equal));
    }

    [Test]
    public void MySqlOption_CharacterSetIsAppliedBeforeCollate()
    {
        var d = new MySqlOptionDescriptor();
        Assert.That(d.ApplyOrder("CHARACTER SET"), Is.LessThan(d.ApplyOrder("COLLATE")));
    }

    [Test]
    public void SqlServerScopedConfiguration_Below2016_CannotBeFixed()
    {
        var reading = new SqlServerScopedConfigurationDescriptor(12).Read(Substitute.For<IDbCommand>(), "MAXDOP", "d");
        Assert.That(reading.UnfixableReason, Is.EqualTo("database-scoped configuration needs SQL Server 2016"));
    }

    [Test]
    public void PostgreSqlParameter_SendsTheValueAsOneLiteral()
    {
        var (cmd, sql, _) = Recorder();
        new PostgreSqlDatabaseParameterDescriptor().Apply(cmd, "application_name", "a', b; DROP", "my\"db");
        Assert.That(sql, Is.EqualTo(new[] { "ALTER DATABASE \"my\"\"db\" SET \"application_name\" = 'a'', b; DROP'" }));
    }

    [Test]
    public void MySqlOption_SendsTheValueAsOneLiteral()
    {
        var (cmd, sql, _) = Recorder();
        new MySqlOptionDescriptor().Apply(cmd, "collate", "utf8mb4_bin'\\; DROP", "my`db");
        Assert.That(sql, Is.EqualTo(new[] { "ALTER DATABASE `my``db` COLLATE = 'utf8mb4_bin''\\\\; DROP'" }));
    }

    [Test]
    public void MySqlOption_OnlyCharacterSetAndCollate()
    {
        var reading = new MySqlOptionDescriptor().Read(Substitute.For<IDbCommand>(), "ENCRYPTION", "d");
        Assert.That(reading.Exists, Is.False);
        Assert.That(reading.UnfixableReason, Does.Contain("CHARACTER SET or COLLATE"));
    }
}
