// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using NUnit.Framework;

namespace SchemaTongs.UnitTests;

// A WMI alert can exist only on a Windows server with SQL Server Agent running, so no container leg can extract one;
// these pin the arguments its script passes, which sp_verify_alert accepts only in the WMI form.
[TestFixture]
public class SqlServerServerObjectCasterTests
{
    private static Dictionary<string, string> Alert(string eventId, string messageId, string severity, string databaseName, string performanceCondition) => new()
    {
        ["name"] = "a1", ["event_id"] = eventId, ["message_id"] = messageId, ["severity"] = severity, ["enabled"] = "1",
        ["delay_between_responses"] = "0", ["notification_message"] = null, ["include_event_description"] = "5",
        ["database_name"] = databaseName, ["event_description_keyword"] = null, ["job_name"] = null, ["performance_condition"] = performanceCondition
    };

    [Test]
    public void AWmiAlert_IsScriptedWithItsNamespaceAndQuery()
    {
        // msdb keeps a WMI alert's namespace in database_name and its query in performance_condition.
        var args = SqlServerServerObjectCaster.AlertArguments(
            Alert("8", "0", "0", @"\\.\root\Microsoft\SqlServer\ServerEvents\MSSQLSERVER", "SELECT * FROM DEADLOCK_GRAPH"));

        Assert.Multiple(() =>
        {
            Assert.That(args, Does.Contain(@"@wmi_namespace = N'\\.\root\Microsoft\SqlServer\ServerEvents\MSSQLSERVER'"));
            Assert.That(args, Does.Contain("@wmi_query = N'SELECT * FROM DEADLOCK_GRAPH'"));
            Assert.That(args, Does.Not.Contain("@database_name"), "the namespace is not a database");
            Assert.That(args, Does.Not.Contain("@performance_condition"), "the query is not a performance condition");
        });
    }

    [Test]
    public void ASeverityAlert_KeepsItsDatabaseAndHasNoWmiArguments()
    {
        var args = SqlServerServerObjectCaster.AlertArguments(Alert("1", "0", "17", "Sales", null));

        Assert.Multiple(() =>
        {
            Assert.That(args, Does.Contain("@severity = 17"));
            Assert.That(args, Does.Contain("@database_name = N'Sales'"));
            Assert.That(args, Does.Not.Contain("@wmi_"));
        });
    }
}
