// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using log4net;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.DisposableServer;

/// <summary>
/// D12 server-level objects on a real SQL Server with SQL Server Agent running. Creating jobs, audits, a server trigger and
/// a running event session changes the server, so this runs only against a throwaway container
/// (scripts/run-server-settings-legs.sh, which sets SCHEMASMITH_DISPOSABLE_SERVER=1). One server plays both parts: the
/// scripts are re-run where they were extracted, and run again after the objects are dropped, as on a second server.
/// </summary>
[NonParallelizable]
[Category("DisposableServerObjects")]
public class ServerObjectCastTests
{
    private string _connectionString;
    private string _productDir;
    private readonly List<string> _messages = [];

    private static IConfigurationRoot TestConfig => ConfigHelper.GetAppSettingsAndUserSecrets("test", null);

    [OneTimeSetUp]
    public void CreateObjectsAndExtract()
    {
        if (Environment.GetEnvironmentVariable("SCHEMASMITH_DISPOSABLE_SERVER") != "1")
            Assert.Ignore("Server objects change the server: run them with scripts/run-server-settings-legs.sh, never against a shared server.");
        var c = TestConfig;
        _connectionString = new SqlConnectionStringBuilder
        {
            DataSource = $"{c["SqlServer:Server"] ?? "127.0.0.1"},{c["SqlServer:Port"]}", UserID = c["SqlServer:User"], Password = c["SqlServer:Password"],
            InitialCatalog = "master", TrustServerCertificate = true
        }.ConnectionString;

        DropTheObjects();
        Exec("""
            EXEC msdb.dbo.sp_add_operator @name = N'ss_op', @enabled = 1, @email_address = N'dba@example.com';
            EXEC msdb.dbo.sp_add_job @job_name = N'ss_job', @enabled = 1, @description = N'two steps', @notify_level_email = 2, @notify_email_operator_name = N'ss_op';
            EXEC msdb.dbo.sp_add_jobstep @job_name = N'ss_job', @step_name = N'first', @subsystem = N'TSQL', @command = N'SELECT 1', @on_success_action = 3;
            EXEC msdb.dbo.sp_add_jobstep @job_name = N'ss_job', @step_name = N'second', @subsystem = N'TSQL', @command = N'SELECT 2',
                 @database_name = N'master', @database_user_name = N'dbo';
            EXEC msdb.dbo.sp_add_jobschedule @job_name = N'ss_job', @name = N'nightly', @freq_type = 4, @freq_interval = 1, @active_start_time = 20000;
            EXEC msdb.dbo.sp_add_jobserver @job_name = N'ss_job';
            EXEC msdb.dbo.sp_add_job @job_name = N'ss_slow';
            EXEC msdb.dbo.sp_add_jobstep @job_name = N'ss_slow', @step_name = N'wait', @subsystem = N'TSQL', @command = N'WAITFOR DELAY ''00:00:40''';
            EXEC msdb.dbo.sp_add_jobserver @job_name = N'ss_slow';
            -- A step whose command holds a character XML cannot carry, and a schedule two jobs share.
            DECLARE @odd NVARCHAR(100) = N'SELECT 3 -- ' + NCHAR(1);
            EXEC msdb.dbo.sp_add_job @job_name = N'ss_ctl';
            EXEC msdb.dbo.sp_add_jobstep @job_name = N'ss_ctl', @step_name = N'odd', @subsystem = N'TSQL', @command = @odd;
            EXEC msdb.dbo.sp_add_jobserver @job_name = N'ss_ctl';
            EXEC msdb.dbo.sp_add_schedule @schedule_name = N'ss_shared', @freq_type = 4, @freq_interval = 1, @active_start_time = 30000;
            EXEC msdb.dbo.sp_attach_schedule @job_name = N'ss_ctl', @schedule_name = N'ss_shared';
            EXEC msdb.dbo.sp_attach_schedule @job_name = N'ss_slow', @schedule_name = N'ss_shared';
            EXEC msdb.dbo.sp_add_alert @name = N'ss_alert', @severity = 17, @job_name = N'ss_job';
            EXEC msdb.dbo.sp_add_notification @alert_name = N'ss_alert', @operator_name = N'ss_op', @notification_method = 1;
            """);
        Exec("CREATE SERVER AUDIT ss_audit TO FILE (FILEPATH = N'/var/opt/mssql/data/', MAX_FILES = 5) WITH (QUEUE_DELAY = 1000, ON_FAILURE = CONTINUE)");
        Exec("CREATE SERVER AUDIT SPECIFICATION ss_spec FOR SERVER AUDIT ss_audit ADD (FAILED_LOGIN_GROUP) WITH (STATE = ON)");
        Exec("ALTER SERVER AUDIT ss_audit WITH (STATE = ON)");
        Exec("CREATE EVENT SESSION ss_xe ON SERVER ADD EVENT sqlserver.error_reported (ACTION (sqlserver.sql_text) WHERE severity >= 16) " +
             "ADD TARGET package0.ring_buffer (SET max_memory = 1024) WITH (STARTUP_STATE = OFF)");
        Exec("ALTER EVENT SESSION ss_xe ON SERVER STATE = START");
        Exec("CREATE TRIGGER ss_ddl ON ALL SERVER FOR CREATE_DATABASE AS PRINT 'database created'");
        // A trigger whose text carries a commented-out CREATE TRIGGER above the real one.
        Exec("/* was: CREATE TRIGGER old_name ON ALL SERVER */\r\nCREATE TRIGGER ss_ddl2 ON ALL SERVER FOR ALTER_DATABASE AS PRINT 'altered'");
        // Two names that map to one file, and a job owned by a login the target will not have.
        Exec("""
            EXEC msdb.dbo.sp_add_job @job_name = N'DBA: Backup';
            EXEC msdb.dbo.sp_add_job @job_name = N'DBA? Backup';
            CREATE LOGIN ss_owner WITH PASSWORD = N'Owner!Pass2026x', CHECK_POLICY = OFF;
            EXEC msdb.dbo.sp_add_job @job_name = N'ss_owned', @owner_login_name = N'ss_owner';
            """);
        // Things that hold a credential, which must never be scripted.
        Exec("CREATE CREDENTIAL ss_cred WITH IDENTITY = N'ss_identity', SECRET = N'ss_secret_value'");
        Exec("EXEC sp_addlinkedserver @server = N'ss_link', @srvproduct = N'SQL Server'");

        _productDir = Path.Join(Path.GetTempPath(), $"ServerObjects_{Guid.NewGuid():N}");
        Extract();
    }

    [OneTimeTearDown]
    public void Cleanup()
    {
        if (_connectionString != null) DropTheObjects();
        if (_productDir != null && Directory.Exists(_productDir) && Environment.GetEnvironmentVariable("SCHEMASMITH_KEEP_PRODUCT") != "1") Directory.Delete(_productDir, true);
    }

    private void DropTheObjects() => Exec("""
        IF EXISTS (SELECT 1 FROM msdb.dbo.sysalerts WHERE name = N'ss_alert') EXEC msdb.dbo.sp_delete_alert @name = N'ss_alert';
        IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'ss_job') EXEC msdb.dbo.sp_delete_job @job_name = N'ss_job';
        IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'ss_slow') EXEC msdb.dbo.sp_delete_job @job_name = N'ss_slow';
        IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'ss_ctl') EXEC msdb.dbo.sp_delete_job @job_name = N'ss_ctl';
        DECLARE @sid INT;
        WHILE 1 = 1
        BEGIN
            SET @sid = (SELECT TOP 1 schedule_id FROM msdb.dbo.sysschedules WHERE name = N'ss_shared');
            IF @sid IS NULL BREAK;
            EXEC msdb.dbo.sp_delete_schedule @schedule_id = @sid, @force_delete = 1;
        END
        IF EXISTS (SELECT 1 FROM msdb.dbo.sysoperators WHERE name = N'ss_op') EXEC msdb.dbo.sp_delete_operator @name = N'ss_op';
        IF EXISTS (SELECT 1 FROM sys.server_audit_specifications WHERE name = N'ss_spec')
        BEGIN
            ALTER SERVER AUDIT SPECIFICATION ss_spec WITH (STATE = OFF);
            DROP SERVER AUDIT SPECIFICATION ss_spec;
        END
        IF EXISTS (SELECT 1 FROM sys.server_audits WHERE name = N'ss_audit')
        BEGIN
            ALTER SERVER AUDIT ss_audit WITH (STATE = OFF);
            DROP SERVER AUDIT ss_audit;
        END
        IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'ss_xe') DROP EVENT SESSION ss_xe ON SERVER;
        IF EXISTS (SELECT 1 FROM sys.server_triggers WHERE name = N'ss_ddl') DROP TRIGGER ss_ddl ON ALL SERVER;
        IF EXISTS (SELECT 1 FROM sys.credentials WHERE name = N'ss_cred') DROP CREDENTIAL ss_cred;
        IF EXISTS (SELECT 1 FROM sys.server_triggers WHERE name = N'ss_ddl2') DROP TRIGGER ss_ddl2 ON ALL SERVER;
        IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'DBA: Backup') EXEC msdb.dbo.sp_delete_job @job_name = N'DBA: Backup';
        IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'DBA? Backup') EXEC msdb.dbo.sp_delete_job @job_name = N'DBA? Backup';
        IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'ss_owned') EXEC msdb.dbo.sp_delete_job @job_name = N'ss_owned';
        IF SUSER_ID(N'ss_owner') IS NOT NULL DROP LOGIN ss_owner;
        IF EXISTS (SELECT 1 FROM sys.servers WHERE name = N'ss_link') EXEC sp_dropserver @server = N'ss_link';
        """);

    private void Exec(string sql)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.InfoMessage += (_, e) => _messages.Add(e.Message);
        conn.Open();
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Where(b => b.Trim().Length > 0))
        {
            using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 120 };
            cmd.ExecuteNonQuery();
        }
    }

    private object Scalar(string sql)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        return cmd.ExecuteScalar();
    }

    private void Extract()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            try
            {
                LogFactory.Register("ErrorLog", Substitute.For<ILog>());
                LogFactory.Register("ProgressLog", Substitute.For<ILog>());
                FactoryContainer.Register(Substitute.For<IEnvironment>());
                var config = TestConfig;
                config["Source:Server"] = config["SqlServer:Server"] ?? "127.0.0.1";
                config["Source:Port"] = config["SqlServer:Port"];
                config["Source:User"] = config["SqlServer:User"];
                config["Source:Password"] = config["SqlServer:Password"];
                config["Source:Database"] = "master";
                config["Source:ConnectionProperties:TrustServerCertificate"] = "True";
                config["Product:Path"] = _productDir;
                config["Product:Name"] = "ServerObjects";
                config["Template:Name"] = "Main";
                foreach (var kind in new[] { "Tables", "Views", "Functions", "Procedures", "TableTriggers", "Schemas", "UserDefinedTypes", "Catalogs", "StopLists",
                             "DDLTriggers", "XMLSchemaCollections", "IndexedViews", "Synonyms", "SecurityPolicies", "Sequences" })
                    config[$"ShouldCast:{kind}"] = "false";
                config["ShouldCast:ServerObjects"] = "true";
                FactoryContainer.Register<IConfigurationRoot>(config);
                new SchemaTongs(Platform.SqlServer).CastTemplate();
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }
    }

    // The product's After folders in their registered order, each script in name order, as a deploy runs them.
    private void RunTheScripts(params string[] only)
    {
        foreach (var folder in SqlServerServerObjectCaster.Folders)
        {
            var dir = Path.Join(_productDir, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
                if (only.Length == 0 || only.Contains(Path.GetFileNameWithoutExtension(file)))
                    Exec(File.ReadAllText(file));
        }
    }

    // One scenario: each step builds on the server the step before left.
    [Test]
    public void ServerObjects_ExtractedAndDeployed_BehaveAsDeclared()
    {
        EveryKind_IsCast_AndRegisteredAsAnAfterFolderForEveryServer();
        RunningTheScriptsWhereNothingChanged_LeavesEveryObjectAlone();
        AJobChangedOnTheServer_IsBroughtBack_KeepingItsIdAndHistory();
        AJobThatSharesASchedule_KeepsItShared();
        ARunningJob_IsLeftAsItIs_AndTheRunSaysSo();
        OnAServerWithoutThem_TheScriptsCreateEveryObject();
    }

    private string JobId(string job) => Scalar($"SELECT CAST(job_id AS NVARCHAR(36)) FROM msdb.dbo.sysjobs WHERE name = N'{job}'")?.ToString();

    private void StartAndWait(string job, Func<bool> until)
    {
        Exec($"EXEC msdb.dbo.sp_start_job @job_name = N'{job}'");
        var watch = Stopwatch.StartNew();
        while (!until() && watch.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(500);
        Assert.That(until(), Is.True, $"job {job} did not get where the test needs it within a minute");
    }

    private void EveryKind_IsCast_AndRegisteredAsAnAfterFolderForEveryServer()
    {
        var product = File.ReadAllText(Path.Join(_productDir, "Product.json"));
        Assert.Multiple(() =>
        {
            foreach (var (folder, file) in new[] { ("ServerTriggers", "ss_ddl"), ("AgentOperators", "ss_op"), ("AgentJobs", "ss_job"), ("AgentAlerts", "ss_alert"),
                         ("ServerAudits", "1-ss_audit"), ("ServerAudits", "2-ss_spec"), ("EventSessions", "ss_xe") })
            {
                Assert.That(File.Exists(Path.Join(_productDir, folder, file + ".sql")), Is.True, $"{folder}/{file}.sql");
                Assert.That(product, Does.Contain($"\"FolderPath\": \"{folder}\""), product);
            }
            Assert.That(product, Does.Contain("\"QuenchSlot\": \"After\"").And.Contain("\"ServerToQuench\": \"Both\""), product);
            Assert.That(File.Exists(Path.Join(_productDir, "EventSessions", "system_health.sql")), Is.False, "the server's own sessions are left out");
            var jobFiles = Directory.GetFiles(Path.Join(_productDir, "AgentJobs")).Select(Path.GetFileName).ToList();
            Assert.That(jobFiles, Has.Member("DBA_ Backup.sql").And.Member("DBA_ Backup~2.sql"), "two names that map to one file both survive");
            var everything = string.Join("\n", Directory.GetFiles(_productDir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.That(everything, Does.Not.Contain("ss_cred").And.Not.Contain("ss_secret_value").And.Not.Contain("ss_link"),
                "nothing that holds a credential is scripted");
        });
    }

    private void RunningTheScriptsWhereNothingChanged_LeavesEveryObjectAlone()
    {
        var jobModified = Scalar("SELECT CONVERT(NVARCHAR(30), date_modified, 126) FROM msdb.dbo.sysjobs WHERE name = N'ss_job'");
        var auditModified = Scalar("SELECT CONVERT(NVARCHAR(30), modify_date, 126) FROM sys.server_audits WHERE name = N'ss_audit'");
        // create_time reads a few milliseconds apart on the same live session; the address is the session instance itself.
        var sessionStarted = Scalar("SELECT CONVERT(NVARCHAR(40), address, 1) FROM sys.dm_xe_sessions WHERE name = N'ss_xe'");

        RunTheScripts();

        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT CONVERT(NVARCHAR(30), date_modified, 126) FROM msdb.dbo.sysjobs WHERE name = N'ss_job'"), Is.EqualTo(jobModified), "the job was not touched");
            Assert.That(Scalar("SELECT CONVERT(NVARCHAR(30), modify_date, 126) FROM sys.server_audits WHERE name = N'ss_audit'"), Is.EqualTo(auditModified), "the audit was not switched off and on");
            Assert.That(Scalar("SELECT CONVERT(NVARCHAR(40), address, 1) FROM sys.dm_xe_sessions WHERE name = N'ss_xe'"), Is.EqualTo(sessionStarted), "the running session was not restarted");
        });
    }

    private void AJobChangedOnTheServer_IsBroughtBack_KeepingItsIdAndHistory()
    {
        StartAndWait("ss_job", () => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM msdb.dbo.sysjobhistory h JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id WHERE j.name = N'ss_job' AND h.step_id = 0")) > 0);
        var id = JobId("ss_job");
        var history = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM msdb.dbo.sysjobhistory h JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id WHERE j.name = N'ss_job'"));
        Exec("EXEC msdb.dbo.sp_update_jobstep @job_name = N'ss_job', @step_id = 1, @command = N'SELECT 99'");

        RunTheScripts("ss_job");

        Assert.Multiple(() =>
        {
            Assert.That(JobId("ss_job"), Is.EqualTo(id), "the job kept its id");
            Assert.That(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM msdb.dbo.sysjobhistory h JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id WHERE j.name = N'ss_job'")),
                Is.GreaterThanOrEqualTo(history), "and its history");
            Assert.That(Scalar("SELECT s.command FROM msdb.dbo.sysjobsteps s JOIN msdb.dbo.sysjobs j ON j.job_id = s.job_id WHERE j.name = N'ss_job' AND s.step_id = 1"),
                Is.EqualTo("SELECT 1"), "the step is back as the package has it");
        });
    }

    private void ARunningJob_IsLeftAsItIs_AndTheRunSaysSo()
    {
        StartAndWait("ss_slow", () => Convert.ToInt32(Scalar("""
            SELECT COUNT(*) FROM msdb.dbo.sysjobactivity a JOIN msdb.dbo.sysjobs j ON j.job_id = a.job_id
             WHERE j.name = N'ss_slow' AND a.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions)
               AND a.start_execution_date IS NOT NULL AND a.stop_execution_date IS NULL
            """)) > 0);
        try
        {
            Exec("EXEC msdb.dbo.sp_update_job @job_name = N'ss_slow', @description = N'changed while running'");
            _messages.Clear();

            RunTheScripts("ss_slow");

            Assert.That(_messages, Has.Some.Contains("Agent job 'ss_slow' is running; left as it is"), string.Join(" | ", _messages));
            Assert.That(Scalar("SELECT description FROM msdb.dbo.sysjobs WHERE name = N'ss_slow'"), Is.EqualTo("changed while running"));
        }
        finally
        {
            Exec("EXEC msdb.dbo.sp_stop_job @job_name = N'ss_slow'");
        }
    }

    private string StepCommand(string job, int step) =>
        Scalar($"SELECT s.command FROM msdb.dbo.sysjobsteps s JOIN msdb.dbo.sysjobs j ON j.job_id = s.job_id WHERE j.name = N'{job}' AND s.step_id = {step}")?.ToString();

    private int SharedScheduleCount() => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM msdb.dbo.sysschedules WHERE name = N'ss_shared'"));
    private int SharedScheduleJobs() => Convert.ToInt32(Scalar(
        "SELECT COUNT(*) FROM msdb.dbo.sysjobschedules js JOIN msdb.dbo.sysschedules sc ON sc.schedule_id = js.schedule_id WHERE sc.name = N'ss_shared'"));

    private void AJobThatSharesASchedule_KeepsItShared()
    {
        Exec("EXEC msdb.dbo.sp_update_jobstep @job_name = N'ss_ctl', @step_id = 1, @retry_attempts = 3");

        RunTheScripts("ss_ctl");

        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT s.retry_attempts FROM msdb.dbo.sysjobsteps s JOIN msdb.dbo.sysjobs j ON j.job_id = s.job_id WHERE j.name = N'ss_ctl'"), Is.EqualTo(0), "the job is back");
            Assert.That(StepCommand("ss_ctl", 1), Is.EqualTo("SELECT 3 -- " + (char)1), "with the character XML cannot carry");
            Assert.That(SharedScheduleCount(), Is.EqualTo(1), "the shared schedule is not copied");
            Assert.That(SharedScheduleJobs(), Is.EqualTo(2), "and both jobs still run on it");
        });
    }

    private void OnAServerWithoutThem_TheScriptsCreateEveryObject()
    {
        Exec("""
            EXEC msdb.dbo.sp_delete_alert @name = N'ss_alert';
            EXEC msdb.dbo.sp_delete_job @job_name = N'ss_job';
            EXEC msdb.dbo.sp_delete_operator @name = N'ss_op';
            ALTER SERVER AUDIT SPECIFICATION ss_spec WITH (STATE = OFF);
            DROP SERVER AUDIT SPECIFICATION ss_spec;
            ALTER SERVER AUDIT ss_audit WITH (STATE = OFF);
            DROP SERVER AUDIT ss_audit;
            DROP EVENT SESSION ss_xe ON SERVER;
            DROP TRIGGER ss_ddl ON ALL SERVER;
            DROP TRIGGER ss_ddl2 ON ALL SERVER;
            EXEC msdb.dbo.sp_delete_job @job_name = N'ss_owned';
            EXEC msdb.dbo.sp_delete_job @job_name = N'ss_ctl';
            DROP LOGIN ss_owner;
            """);

        RunTheScripts();

        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT email_address FROM msdb.dbo.sysoperators WHERE name = N'ss_op'"), Is.EqualTo("dba@example.com"));
            Assert.That(Scalar("SELECT COUNT(*) FROM msdb.dbo.sysjobsteps s JOIN msdb.dbo.sysjobs j ON j.job_id = s.job_id WHERE j.name = N'ss_job'"), Is.EqualTo(2));
            Assert.That(Scalar("SELECT COUNT(*) FROM msdb.dbo.sysjobschedules js JOIN msdb.dbo.sysjobs j ON j.job_id = js.job_id WHERE j.name = N'ss_job'"), Is.EqualTo(1));
            Assert.That(Scalar("SELECT o.name FROM msdb.dbo.sysjobs j JOIN msdb.dbo.sysoperators o ON o.id = j.notify_email_operator_id WHERE j.name = N'ss_job'"), Is.EqualTo("ss_op"));
            Assert.That(Scalar("SELECT COUNT(*) FROM msdb.dbo.sysnotifications n JOIN msdb.dbo.sysalerts a ON a.id = n.alert_id WHERE a.name = N'ss_alert'"), Is.EqualTo(1));
            Assert.That(Scalar("SELECT CAST(is_state_enabled AS INT) FROM sys.server_audits WHERE name = N'ss_audit'"), Is.EqualTo(1));
            Assert.That(Scalar("SELECT f.max_files FROM sys.server_file_audits f JOIN sys.server_audits a ON a.audit_id = f.audit_id WHERE a.name = N'ss_audit'"), Is.EqualTo(5), "with its file limit");
            Assert.That(SharedScheduleCount(), Is.EqualTo(1), "a job created where its shared schedule exists takes that one");
            Assert.That(SharedScheduleJobs(), Is.EqualTo(2));
            Assert.That(Scalar("SELECT d.audit_action_name FROM sys.server_audit_specification_details d JOIN sys.server_audit_specifications s ON s.server_specification_id = d.server_specification_id WHERE s.name = N'ss_spec'"),
                Is.EqualTo("FAILED_LOGIN_GROUP"));
            Assert.That(Scalar("SELECT COUNT(*) FROM sys.server_event_session_events e JOIN sys.server_event_sessions s ON s.event_session_id = e.event_session_id WHERE s.name = N'ss_xe' AND e.name = N'error_reported'"),
                Is.EqualTo(1));
            Assert.That(Scalar("SELECT COUNT(*) FROM sys.server_triggers WHERE name = N'ss_ddl'"), Is.EqualTo(1));
            Assert.That(Scalar("SELECT COUNT(*) FROM sys.server_triggers WHERE name = N'ss_ddl2'"), Is.EqualTo(1), "and the one with a commented-out header");
            Assert.That(Scalar("SELECT s.database_user_name FROM msdb.dbo.sysjobsteps s JOIN msdb.dbo.sysjobs j ON j.job_id = s.job_id WHERE j.name = N'ss_job' AND s.step_id = 2"),
                Is.EqualTo("dbo"), "a step keeps the user it runs as");
            Assert.That(Scalar("SELECT COUNT(*) FROM msdb.dbo.sysjobs WHERE name = N'ss_owned'"), Is.EqualTo(1), "a job whose owner this server lacks is still created");
        });

        // And a deploy after that changes nothing more: the scripts reach the state they describe.
        var jobModified = Scalar("SELECT CONVERT(NVARCHAR(30), date_modified, 126) FROM msdb.dbo.sysjobs WHERE name = N'ss_job'");
        RunTheScripts();
        Assert.That(Scalar("SELECT CONVERT(NVARCHAR(30), date_modified, 126) FROM msdb.dbo.sysjobs WHERE name = N'ss_job'"), Is.EqualTo(jobModified),
            "a job the scripts created matches its signature");
    }
}
