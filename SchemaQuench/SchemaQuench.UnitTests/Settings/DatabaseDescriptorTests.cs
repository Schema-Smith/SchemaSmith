// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using NSubstitute;
using Schema.Domain;
using Schema.Domain.Settings;
using SchemaQuench.Settings;

namespace SchemaQuench.UnitTests.Settings;

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
        Assert.That(sql, Is.EqualTo(new[] { "ALTER DATABASE SCOPED CONFIGURATION SET [MAXDOP] = 4" }));
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
        new PostgreSqlDatabaseParameterDescriptor().Apply(cmd, "search_path", "a', b; DROP", "my\"db");
        Assert.That(sql, Is.EqualTo(new[] { "ALTER DATABASE \"my\"\"db\" SET \"search_path\" = 'a'', b; DROP'" }));
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
