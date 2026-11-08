// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SchemaTongs;

public sealed record ServerObjectScript(string Folder, string FileName, string Sql);

/// <summary>
/// D12: SQL Server's server-level scripted objects as one idempotent script each, for product After folders that run on
/// every deploy. Jobs, audits, audit specifications and event sessions carry a signature taken from the source's catalog
/// by the same query the script runs on the target, so an unchanged object is left alone: a job keeps its steps and
/// history, an audit is not switched off and on, a running session is not restarted. Triggers, operators and alerts are
/// cheap to re-apply and are. Nothing carrying a credential is scripted, and nothing is ever dropped by absence.
/// </summary>
public static class SqlServerServerObjectCaster
{
    // In dependency order: an alert names a job and an operator, a job names an operator, a specification an audit.
    public static readonly string[] Folders = ["ServerTriggers", "AgentOperators", "AgentJobs", "AgentAlerts", "ServerAudits", "EventSessions"];

    public static IReadOnlyList<ServerObjectScript> Cast(IDbCommand cmd, int serverMajorVersion, Action<string> warn) =>
        Distinct(Triggers(cmd, warn).Concat(Operators(cmd)).Concat(Jobs(cmd, warn)).Concat(Alerts(cmd)).Concat(Audits(cmd, serverMajorVersion, warn)).Concat(EventSessions(cmd)));

    // Two names can map to one file ("DBA: Backup" and "DBA? Backup", or a difference of case): the later one gets a suffix
    // rather than overwriting the first.
    private static List<ServerObjectScript> Distinct(IEnumerable<ServerObjectScript> scripts)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ServerObjectScript>();
        foreach (var script in scripts)
        {
            var name = script.FileName;
            var n = 1;
            while (!used.Add($"{script.Folder}/{name}"))
                name = script.FileName[..^4] + $"~{++n}.sql";
            result.Add(script with { FileName = name });
        }
        return result;
    }

    // ---- helpers ----

    private static string N(string value) => value == null ? "NULL" : "N'" + value.Replace("'", "''") + "'";
    private static string Q(string name) => "[" + name.Replace("]", "]]") + "]";
    private static string Lines(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    internal static string FileNameFor(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()) + ".sql";
    }

    private static List<Dictionary<string, string>> Rows(IDbCommand cmd, string sql, params (string Name, object Value)[] parameters)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        var rows = new List<Dictionary<string, string>>();
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                rows.Add(row);
            }
        }
        finally
        {
            cmd.Parameters.Clear();
        }
        return rows;
    }

    private static string Signature(IDbCommand cmd, string signatureSql, string name)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = $"DECLARE @name SYSNAME = {N(name)};\r\nSELECT {signatureSql};";
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    // The script recomputes the same signature on the target; a NULL there (no such object) never equals the captured one.
    private static string IfChanged(string signatureSql, string name, string captured, string body) => Lines(
        $"DECLARE @name SYSNAME = {N(name)};",
        $"IF ISNULL(({signatureSql}), N'') <> {N(captured)}",
        "BEGIN",
        body.TrimEnd(),
        "END");

    // ---- server triggers (DDL and logon) ----

    private static IEnumerable<ServerObjectScript> Triggers(IDbCommand cmd, Action<string> warn)
    {
        foreach (var t in Rows(cmd, """
                     SELECT t.name, m.definition, CAST(t.is_disabled AS INT) AS is_disabled, CAST(m.uses_ansi_nulls AS INT) AS ansi_nulls,
                            CAST(m.uses_quoted_identifier AS INT) AS quoted_identifier
                       FROM sys.server_triggers t JOIN sys.server_sql_modules m ON m.object_id = t.object_id
                      WHERE t.is_ms_shipped = 0 AND t.type = 'TR'
                     """))
        {
            var name = t["name"];
            if (t["definition"] == null)
            {
                warn($"  Server trigger '{name}' is encrypted, so its definition cannot be read; it is left out.");
                continue;
            }
            var definition = t["definition"].Trim();
            // Matched outside comments and strings: a commented-out CREATE TRIGGER above the real one must not be the one rewritten.
            var header = Regex.Match(SqlTextMask.Blank(definition), @"(?<!\w)CREATE\s+TRIGGER\b", RegexOptions.IgnoreCase);
            if (!header.Success) continue;
            var altered = definition[..header.Index] + "ALTER TRIGGER" + definition[(header.Index + header.Length)..];
            var logon = Regex.IsMatch(definition, @"\b(FOR|AFTER)\s+LOGON\b", RegexOptions.IgnoreCase);
            var stub = $"CREATE TRIGGER {Q(name)} ON ALL SERVER {(logon ? "FOR LOGON" : "FOR DDL_SERVER_LEVEL_EVENTS")} AS RETURN";
            var sql = new StringBuilder()
                .Append($"SET ANSI_NULLS {(t["ansi_nulls"] == "1" ? "ON" : "OFF")}\r\nSET QUOTED_IDENTIFIER {(t["quoted_identifier"] == "1" ? "ON" : "OFF")}\r\nGO\r\n\r\n")
                // A stub, then ALTER, so the script runs on every version and a re-run changes only the body.
                .Append($"IF NOT EXISTS (SELECT 1 FROM sys.server_triggers WHERE name = {N(name)}) EXEC({N(stub)})\r\nGO\r\n\r\n")
                .Append(altered.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\nGO\r\n\r\n")
                .Append($"{(t["is_disabled"] == "1" ? "DISABLE" : "ENABLE")} TRIGGER {Q(name)} ON ALL SERVER\r\nGO\r\n");
            yield return new ServerObjectScript("ServerTriggers", FileNameFor(name), sql.ToString());
        }
    }

    // ---- Agent operators ----

    private static IEnumerable<ServerObjectScript> Operators(IDbCommand cmd)
    {
        foreach (var o in Rows(cmd, """
                     SELECT name, enabled, email_address, pager_address, netsend_address, pager_days,
                            weekday_pager_start_time, weekday_pager_end_time, saturday_pager_start_time, saturday_pager_end_time,
                            sunday_pager_start_time, sunday_pager_end_time
                       FROM msdb.dbo.sysoperators
                     """))
        {
            var args = $"@name = {N(o["name"])}, @enabled = {o["enabled"]}, @email_address = {N(o["email_address"] ?? "")}, " +
                       $"@pager_address = {N(o["pager_address"] ?? "")}, @netsend_address = {N(o["netsend_address"] ?? "")}, @pager_days = {o["pager_days"]}, " +
                       $"@weekday_pager_start_time = {o["weekday_pager_start_time"]}, @weekday_pager_end_time = {o["weekday_pager_end_time"]}, " +
                       $"@saturday_pager_start_time = {o["saturday_pager_start_time"]}, @saturday_pager_end_time = {o["saturday_pager_end_time"]}, " +
                       $"@sunday_pager_start_time = {o["sunday_pager_start_time"]}, @sunday_pager_end_time = {o["sunday_pager_end_time"]}";
            var sql = Lines(
                $"IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysoperators WHERE name = {N(o["name"])})",
                $"    EXEC msdb.dbo.sp_add_operator {args};",
                "ELSE",
                $"    EXEC msdb.dbo.sp_update_operator {args};");
            yield return new ServerObjectScript("AgentOperators", FileNameFor(o["name"]), sql);
        }
    }

    // ---- Agent jobs ----

    internal const string JobSignature = """
        SELECT CAST(j.enabled AS NVARCHAR(1)) + N'|' + ISNULL(j.description, N'') + N'|' + ISNULL(c.name, N'')
               + N'|' + CAST(j.notify_level_eventlog AS NVARCHAR(10)) + N'|' + CAST(j.notify_level_email AS NVARCHAR(10)) + N'|' + CAST(j.notify_level_page AS NVARCHAR(10))
               + N'|' + CAST(j.delete_level AS NVARCHAR(10)) + N'|' + CASE WHEN j.notify_level_email > 0 THEN ISNULL(oe.name, N'') ELSE N'' END
               + N'|' + CASE WHEN j.notify_level_page > 0 THEN ISNULL(op.name, N'') ELSE N'' END + N'|' + CAST(j.start_step_id AS NVARCHAR(10))
               + N'|' + ISNULL((SELECT CAST(s.step_id AS NVARCHAR(10)) + N':' + s.step_name + N':' + s.subsystem + N':' + ISNULL(s.command, N'') + N':' + ISNULL(s.database_name, N'')
                            + N':' + CAST(s.on_success_action AS NVARCHAR(10)) + N':' + CAST(s.on_success_step_id AS NVARCHAR(10)) + N':' + CAST(s.on_fail_action AS NVARCHAR(10))
                            + N':' + CAST(s.on_fail_step_id AS NVARCHAR(10)) + N':' + CAST(s.retry_attempts AS NVARCHAR(10)) + N':' + CAST(s.retry_interval AS NVARCHAR(10))
                            + N':' + ISNULL(s.output_file_name, N'') + N':' + CAST(s.flags AS NVARCHAR(10)) + N':' + ISNULL(s.database_user_name, N'')
                            + N':' + CAST(s.cmdexec_success_code AS NVARCHAR(10)) + N';'
                       FROM msdb.dbo.sysjobsteps s WHERE s.job_id = j.job_id ORDER BY s.step_id FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), N'')
               + N'|' + ISNULL((SELECT sc.name + N':' + CAST(sc.enabled AS NVARCHAR(1)) + N':' + CAST(sc.freq_type AS NVARCHAR(10)) + N':' + CAST(sc.freq_interval AS NVARCHAR(10))
                            + N':' + CAST(sc.freq_subday_type AS NVARCHAR(10)) + N':' + CAST(sc.freq_subday_interval AS NVARCHAR(10)) + N':' + CAST(sc.freq_relative_interval AS NVARCHAR(10))
                            + N':' + CAST(sc.freq_recurrence_factor AS NVARCHAR(10)) + N':' + CAST(sc.active_start_date AS NVARCHAR(10)) + N':' + CAST(sc.active_end_date AS NVARCHAR(10))
                            + N':' + CAST(sc.active_start_time AS NVARCHAR(10)) + N':' + CAST(sc.active_end_time AS NVARCHAR(10)) + N';'
                       FROM msdb.dbo.sysjobschedules js JOIN msdb.dbo.sysschedules sc ON sc.schedule_id = js.schedule_id
                      WHERE js.job_id = j.job_id ORDER BY sc.name FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), N'')
          FROM msdb.dbo.sysjobs j
          LEFT JOIN msdb.dbo.syscategories c ON c.category_id = j.category_id
          LEFT JOIN msdb.dbo.sysoperators oe ON oe.id = j.notify_email_operator_id
          LEFT JOIN msdb.dbo.sysoperators op ON op.id = j.notify_page_operator_id
         WHERE j.name = @name
        """;

    private static IEnumerable<ServerObjectScript> Jobs(IDbCommand cmd, Action<string> warn)
    {
        var jobs = Rows(cmd, """
            SELECT CAST(j.job_id AS NVARCHAR(36)) AS job_id, j.name, j.enabled, j.description, c.name AS category, SUSER_SNAME(j.owner_sid) AS owner,
                   j.notify_level_eventlog, j.notify_level_email, j.notify_level_page, j.delete_level, oe.name AS email_operator, op.name AS page_operator, j.start_step_id
              FROM msdb.dbo.sysjobs j
              LEFT JOIN msdb.dbo.syscategories c ON c.category_id = j.category_id
              LEFT JOIN msdb.dbo.sysoperators oe ON oe.id = j.notify_email_operator_id
              LEFT JOIN msdb.dbo.sysoperators op ON op.id = j.notify_page_operator_id
            """);
        foreach (var j in jobs)
        {
            var name = j["name"];
            var steps = Rows(cmd, """
                SELECT step_id, step_name, subsystem, command, database_name, on_success_action, on_success_step_id, on_fail_action, on_fail_step_id,
                       retry_attempts, retry_interval, output_file_name, flags, proxy_id, database_user_name, cmdexec_success_code
                  FROM msdb.dbo.sysjobsteps WHERE job_id = CAST(@job AS UNIQUEIDENTIFIER) ORDER BY step_id
                """, ("@job", j["job_id"]));
            var schedules = Rows(cmd, """
                SELECT sc.name, sc.enabled, sc.freq_type, sc.freq_interval, sc.freq_subday_type, sc.freq_subday_interval, sc.freq_relative_interval,
                       sc.freq_recurrence_factor, sc.active_start_date, sc.active_end_date, sc.active_start_time, sc.active_end_time
                  FROM msdb.dbo.sysjobschedules js JOIN msdb.dbo.sysschedules sc ON sc.schedule_id = js.schedule_id
                 WHERE js.job_id = CAST(@job AS UNIQUEIDENTIFIER) ORDER BY sc.name
                """, ("@job", j["job_id"]));
            foreach (var s in steps.Where(s => s["proxy_id"] is { } p && p != "0"))
                warn($"  Agent job '{name}' step '{s["step_name"]}' runs under a proxy, which holds a credential: the step is cast without it.");

            var jobArgs = $"@enabled = {j["enabled"]}, @description = {N(j["description"] ?? "")}, @category_name = {N(j["category"] ?? "[Uncategorized (Local)]")}, " +
                          "@owner_login_name = @owner, " +
                          $"@notify_level_eventlog = {j["notify_level_eventlog"]}, @notify_level_email = {j["notify_level_email"]}, " +
                          $"@notify_level_page = {j["notify_level_page"]}, @delete_level = {j["delete_level"]}" +
                          // An operator only where one is named: sp_verify_job refuses an empty name, and a level of 0 already says "never".
                          (j["email_operator"] is { } emailOperator ? $", @notify_email_operator_name = {N(emailOperator)}" : "") +
                          (j["page_operator"] is { } pageOperator ? $", @notify_page_operator_name = {N(pageOperator)}" : "");
            var body = new StringBuilder();
            body.Append(Lines(
                "    DECLARE @job_id UNIQUEIDENTIFIER = (SELECT job_id FROM msdb.dbo.sysjobs WHERE name = @name);",
                "    -- The owner is a login of the source server: kept where this server has it, otherwise the job keeps or takes the deploying login.",
                $"    DECLARE @owner SYSNAME = CASE WHEN SUSER_ID({N(j["owner"] ?? "")}) IS NOT NULL THEN {N(j["owner"] ?? "")} END;",
                "    -- A running job is left as it is; the next deploy brings it into line.",
                "    IF @job_id IS NOT NULL AND EXISTS (SELECT 1 FROM msdb.dbo.sysjobactivity a WHERE a.job_id = @job_id",
                "               AND a.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions) AND a.start_execution_date IS NOT NULL AND a.stop_execution_date IS NULL)",
                $"        RAISERROR(N'Agent job ''%s'' is running; left as it is', 10, 1, {N(name)}) WITH NOWAIT;",
                "    ELSE",
                "    BEGIN"));
            if (j["category"] is { } category)
                body.Append(Lines(
                    $"        IF NOT EXISTS (SELECT 1 FROM msdb.dbo.syscategories WHERE name = {N(category)} AND category_class = 1)",
                    $"            EXEC msdb.dbo.sp_add_category @class = N'JOB', @type = N'LOCAL', @name = {N(category)};"));
            body.Append(Lines(
                "        -- Updated in place when it exists, so its job_id, and with it the run history, stays attached.",
                "        IF @job_id IS NULL",
                $"            EXEC msdb.dbo.sp_add_job @job_name = @name, @job_id = @job_id OUTPUT, {jobArgs};",
                "        ELSE",
                $"            EXEC msdb.dbo.sp_update_job @job_id = @job_id, {jobArgs};",
                "        IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobsteps WHERE job_id = @job_id) EXEC msdb.dbo.sp_delete_jobstep @job_id = @job_id, @step_id = 0;"));
            foreach (var s in steps)
                body.Append(Lines(
                    $"        EXEC msdb.dbo.sp_add_jobstep @job_id = @job_id, @step_id = {s["step_id"]}, @step_name = {N(s["step_name"])}, @subsystem = {N(s["subsystem"])},",
                    $"             @command = {N(s["command"] ?? "")}, @database_name = {N(s["database_name"])}, @on_success_action = {s["on_success_action"]},",
                    $"             @on_success_step_id = {s["on_success_step_id"]}, @on_fail_action = {s["on_fail_action"]}, @on_fail_step_id = {s["on_fail_step_id"]},",
                    $"             @retry_attempts = {s["retry_attempts"]}, @retry_interval = {s["retry_interval"]}, @output_file_name = {N(s["output_file_name"])}, @flags = {s["flags"]},",
                    $"             @database_user_name = {N(s["database_user_name"])}, @cmdexec_success_code = {s["cmdexec_success_code"]};"));
            if (steps.Count > 0)
                body.Append(Lines($"        EXEC msdb.dbo.sp_update_job @job_id = @job_id, @start_step_id = {j["start_step_id"]};"));
            body.Append(Lines(
                "        DECLARE @schedule_id INT;",
                "        WHILE 1 = 1",
                "        BEGIN",
                "            SET @schedule_id = (SELECT TOP 1 schedule_id FROM msdb.dbo.sysjobschedules WHERE job_id = @job_id);",
                "            IF @schedule_id IS NULL BREAK;",
                "            EXEC msdb.dbo.sp_detach_schedule @job_id = @job_id, @schedule_id = @schedule_id, @delete_unused_schedule = 1;",
                "        END"));
            foreach (var sc in schedules)
                body.Append(Lines(
                    $"        EXEC msdb.dbo.sp_add_jobschedule @job_id = @job_id, @name = {N(sc["name"])}, @enabled = {sc["enabled"]}, @freq_type = {sc["freq_type"]},",
                    $"             @freq_interval = {sc["freq_interval"]}, @freq_subday_type = {sc["freq_subday_type"]}, @freq_subday_interval = {sc["freq_subday_interval"]},",
                    $"             @freq_relative_interval = {sc["freq_relative_interval"]}, @freq_recurrence_factor = {sc["freq_recurrence_factor"]},",
                    $"             @active_start_date = {sc["active_start_date"]}, @active_end_date = {sc["active_end_date"]}, @active_start_time = {sc["active_start_time"]}, @active_end_time = {sc["active_end_time"]};"));
            body.Append(Lines(
                "        IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobservers WHERE job_id = @job_id) EXEC msdb.dbo.sp_add_jobserver @job_id = @job_id, @server_name = N'(local)';",
                "    END"));
            yield return new ServerObjectScript("AgentJobs", FileNameFor(name), IfChanged(JobSignature, name, Signature(cmd, $"({JobSignature})", name), body.ToString()));
        }
    }

    // ---- Agent alerts ----

    private static IEnumerable<ServerObjectScript> Alerts(IDbCommand cmd)
    {
        var alerts = Rows(cmd, """
            SELECT a.id, a.name, a.event_id, a.message_id, a.severity, a.enabled, a.delay_between_responses, a.notification_message, a.include_event_description,
                   a.database_name, a.event_description_keyword, j.name AS job_name, a.performance_condition
              FROM msdb.dbo.sysalerts a LEFT JOIN msdb.dbo.sysjobs j ON j.job_id = a.job_id
            """);
        foreach (var a in alerts)
        {
            var args = AlertArguments(a);
            var sql = new StringBuilder(Lines(
                $"IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysalerts WHERE name = {N(a["name"])})",
                $"    EXEC msdb.dbo.sp_add_alert {args};",
                "ELSE",
                $"    EXEC msdb.dbo.sp_update_alert {args};"));
            foreach (var n in Rows(cmd, "SELECT o.name, n.notification_method FROM msdb.dbo.sysnotifications n JOIN msdb.dbo.sysoperators o ON o.id = n.operator_id WHERE n.alert_id = @id",
                         ("@id", int.Parse(a["id"], CultureInfo.InvariantCulture))))
                sql.Append(Lines(
                    $"IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysnotifications n JOIN msdb.dbo.sysalerts a ON a.id = n.alert_id JOIN msdb.dbo.sysoperators o ON o.id = n.operator_id",
                    $"                WHERE a.name = {N(a["name"])} AND o.name = {N(n["name"])})",
                    $"    EXEC msdb.dbo.sp_add_notification @alert_name = {N(a["name"])}, @operator_name = {N(n["name"])}, @notification_method = {n["notification_method"]};",
                    "ELSE",
                    $"    EXEC msdb.dbo.sp_update_notification @alert_name = {N(a["name"])}, @operator_name = {N(n["name"])}, @notification_method = {n["notification_method"]};"));
            yield return new ServerObjectScript("AgentAlerts", FileNameFor(a["name"]), sql.ToString());
        }
    }

    // msdb keeps a WMI alert (event_id 8) with its namespace in database_name and its query in performance_condition.
    internal static string AlertArguments(IReadOnlyDictionary<string, string> a)
    {
        var wmi = a["event_id"] == "8";
        return $"@name = {N(a["name"])}, @message_id = {a["message_id"]}, @severity = {a["severity"]}, @enabled = {a["enabled"]}, " +
               $"@delay_between_responses = {a["delay_between_responses"]}, @notification_message = {N(a["notification_message"] ?? "")}, " +
               $"@include_event_description_in = {a["include_event_description"]}, " +
               (wmi ? $"@wmi_namespace = {N(a["database_name"])}, @wmi_query = {N(a["performance_condition"])}, "
                    : $"@database_name = {N(a["database_name"] ?? "")}, ") +
               $"@event_description_keyword = {N(a["event_description_keyword"] ?? "")}" +
               (a["job_name"] is { } job ? $", @job_name = {N(job)}" : "") +
               (wmi || string.IsNullOrEmpty(a["performance_condition"]) ? "" : $", @performance_condition = {N(a["performance_condition"])}");
    }

    // ---- server audits and audit specifications ----

    internal static string AuditSignature(int major) => $"""
        SELECT a.type COLLATE DATABASE_DEFAULT + N'|' + a.on_failure_desc COLLATE DATABASE_DEFAULT + N'|' + CAST(a.queue_delay AS NVARCHAR(10)) + N'|' + ISNULL(f.log_file_path COLLATE DATABASE_DEFAULT, N'')
               + N'|' + ISNULL(CAST(f.max_file_size AS NVARCHAR(20)), N'') + N'|' + ISNULL(CAST(f.max_rollover_files AS NVARCHAR(20)), N'')
               + N'|' + ISNULL(CAST(f.reserve_disk_space AS NVARCHAR(1)), N'') + N'|' + {(major >= 11 ? "ISNULL(a.predicate COLLATE DATABASE_DEFAULT, N'')" : "N''")}
               + N'|' + CAST(a.is_state_enabled AS NVARCHAR(1))
          FROM sys.server_audits a LEFT JOIN sys.server_file_audits f ON f.audit_id = a.audit_id
         WHERE a.name = @name
        """;

    internal const string SpecificationSignature = """
        SELECT a.name COLLATE DATABASE_DEFAULT + N'|' + CAST(s.is_state_enabled AS NVARCHAR(1)) + N'|'
               + ISNULL((SELECT d.audit_action_name COLLATE DATABASE_DEFAULT + N',' FROM sys.server_audit_specification_details d
                          WHERE d.server_specification_id = s.server_specification_id ORDER BY d.audit_action_name FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), N'')
          FROM sys.server_audit_specifications s JOIN sys.server_audits a ON a.audit_guid = s.audit_guid
         WHERE s.name = @name
        """;

    private static IEnumerable<ServerObjectScript> Audits(IDbCommand cmd, int major, Action<string> warn)
    {
        var audits = Rows(cmd, $"""
            SELECT a.name, a.type, CAST(a.audit_guid AS NVARCHAR(36)) AS audit_guid, a.on_failure_desc, a.queue_delay, CAST(a.is_state_enabled AS INT) AS enabled,
                   {(major >= 11 ? "a.predicate" : "CAST(NULL AS NVARCHAR(MAX)) AS predicate")}, f.log_file_path, f.max_file_size, f.max_rollover_files,
                   CAST(f.reserve_disk_space AS INT) AS reserve_disk_space
              FROM sys.server_audits a LEFT JOIN sys.server_file_audits f ON f.audit_id = a.audit_id
            """);
        foreach (var a in audits)
        {
            var name = a["name"];
            string target;
            switch (a["type"])
            {
                case "FL":
                    var size = a["max_file_size"] is "0" or null ? "UNLIMITED" : $"{a["max_file_size"]} MB";
                    var rollover = a["max_rollover_files"] is "2147483647" or null ? "UNLIMITED" : a["max_rollover_files"];
                    target = $"TO FILE (FILEPATH = {N(a["log_file_path"])}, MAXSIZE = {size}, MAX_ROLLOVER_FILES = {rollover}, RESERVE_DISK_SPACE = {(a["reserve_disk_space"] == "1" ? "ON" : "OFF")})";
                    break;
                case "SL": target = "TO SECURITY_LOG"; break;
                case "AL": target = "TO APPLICATION_LOG"; break;
                default:
                    warn($"  Server audit '{name}' writes to a target type {a["type"]} that is not scripted; it is left out.");
                    continue;
            }
            var onFailure = a["on_failure_desc"] switch { "SHUTDOWN SERVER INSTANCE" => "SHUTDOWN", "FAIL OPERATION" => "FAIL_OPERATION", _ => "CONTINUE" };
            var where = string.IsNullOrEmpty(a["predicate"]) ? "" : $" WHERE {a["predicate"]}";
            var state = a["enabled"] == "1" ? "ON" : "OFF";
            var body = Lines(
                "    IF NOT EXISTS (SELECT 1 FROM sys.server_audits WHERE name = @name)",
                $"        EXEC({N($"CREATE SERVER AUDIT {Q(name)} {target} WITH (QUEUE_DELAY = {a["queue_delay"]}, ON_FAILURE = {onFailure}, AUDIT_GUID = '{a["audit_guid"]}'){where}")});",
                "    ELSE",
                "    BEGIN",
                $"        EXEC({N($"ALTER SERVER AUDIT {Q(name)} WITH (STATE = OFF)")});",
                $"        EXEC({N($"ALTER SERVER AUDIT {Q(name)} {target} WITH (QUEUE_DELAY = {a["queue_delay"]}, ON_FAILURE = {onFailure})")});",
                where.Length > 0 ? $"        EXEC({N($"ALTER SERVER AUDIT {Q(name)}{where}")});" : $"        IF EXISTS (SELECT 1 FROM sys.server_audits WHERE name = @name AND predicate IS NOT NULL) EXEC({N($"ALTER SERVER AUDIT {Q(name)} REMOVE WHERE")});",
                "    END",
                $"    EXEC({N($"ALTER SERVER AUDIT {Q(name)} WITH (STATE = {state})")});");
            if (major < 11) body = body.Replace("AND predicate IS NOT NULL", "AND 1 = 0");
            yield return new ServerObjectScript("ServerAudits", "1-" + FileNameFor(name), IfChanged(AuditSignature(major), name, Signature(cmd, $"({AuditSignature(major)})", name), body));
        }

        var specifications = Rows(cmd, """
            SELECT s.server_specification_id, s.name, a.name AS audit_name, CAST(s.is_state_enabled AS INT) AS enabled
              FROM sys.server_audit_specifications s JOIN sys.server_audits a ON a.audit_guid = s.audit_guid
            """);
        foreach (var s in specifications)
        {
            var name = s["name"];
            var groups = Rows(cmd, "SELECT audit_action_name FROM sys.server_audit_specification_details WHERE server_specification_id = @id ORDER BY audit_action_name",
                ("@id", int.Parse(s["server_specification_id"], CultureInfo.InvariantCulture))).Select(r => r["audit_action_name"]).ToList();
            var adds = string.Join(", ", groups.Select(g => $"ADD ({g})"));
            var body = Lines(
                "    IF EXISTS (SELECT 1 FROM sys.server_audit_specifications WHERE name = @name)",
                "    BEGIN",
                $"        EXEC({N($"ALTER SERVER AUDIT SPECIFICATION {Q(name)} WITH (STATE = OFF)")});",
                $"        EXEC({N($"DROP SERVER AUDIT SPECIFICATION {Q(name)}")});",
                "    END",
                $"    EXEC({N($"CREATE SERVER AUDIT SPECIFICATION {Q(name)} FOR SERVER AUDIT {Q(s["audit_name"])}{(adds.Length > 0 ? " " + adds : "")} WITH (STATE = {(s["enabled"] == "1" ? "ON" : "OFF")})")});");
            yield return new ServerObjectScript("ServerAudits", "2-" + FileNameFor(name), IfChanged(SpecificationSignature, name, Signature(cmd, $"({SpecificationSignature})", name), body));
        }
    }

    // ---- Extended Events sessions ----

    internal const string EventSessionSignature = """
        SELECT s.event_retention_mode_desc COLLATE DATABASE_DEFAULT + N'|' + CAST(s.max_dispatch_latency AS NVARCHAR(20)) + N'|' + CAST(s.max_memory AS NVARCHAR(20))
               + N'|' + CAST(s.max_event_size AS NVARCHAR(20)) + N'|' + s.memory_partition_mode_desc COLLATE DATABASE_DEFAULT + N'|' + CAST(s.track_causality AS NVARCHAR(1))
               + N'|' + CAST(s.startup_state AS NVARCHAR(1)) + N'|'
               + ISNULL((SELECT e.package COLLATE DATABASE_DEFAULT + N'.' + e.name COLLATE DATABASE_DEFAULT + N'(' + ISNULL(e.predicate COLLATE DATABASE_DEFAULT, N'') + N')['
                               + ISNULL((SELECT x.package COLLATE DATABASE_DEFAULT + N'.' + x.name COLLATE DATABASE_DEFAULT + N',' FROM sys.server_event_session_actions x
                                          WHERE x.event_session_id = e.event_session_id AND x.event_id = e.event_id ORDER BY x.package, x.name FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), N'')
                               + N']{' + ISNULL((SELECT f.name COLLATE DATABASE_DEFAULT + N'=' + CAST(f.value AS NVARCHAR(4000)) COLLATE DATABASE_DEFAULT + N',' FROM sys.server_event_session_fields f
                                          WHERE f.event_session_id = e.event_session_id AND f.object_id = e.event_id ORDER BY f.name FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), N'')
                               + N'};'
                          FROM sys.server_event_session_events e WHERE e.event_session_id = s.event_session_id ORDER BY e.package, e.name FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), N'')
               + N'|'
               + ISNULL((SELECT t.package COLLATE DATABASE_DEFAULT + N'.' + t.name COLLATE DATABASE_DEFAULT + N'{'
                               + ISNULL((SELECT f.name COLLATE DATABASE_DEFAULT + N'=' + CAST(f.value AS NVARCHAR(4000)) COLLATE DATABASE_DEFAULT + N',' FROM sys.server_event_session_fields f
                                          WHERE f.event_session_id = t.event_session_id AND f.object_id = t.target_id ORDER BY f.name FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), N'')
                               + N'};'
                          FROM sys.server_event_session_targets t WHERE t.event_session_id = s.event_session_id ORDER BY t.package, t.name FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), N'')
          FROM sys.server_event_sessions s
         WHERE s.name = @name
        """;

    private static IEnumerable<ServerObjectScript> EventSessions(IDbCommand cmd)
    {
        // The sessions SQL Server ships with are the server's own, re-created by its setup, not a package's.
        var sessions = Rows(cmd, """
            SELECT event_session_id, name, event_retention_mode_desc, max_dispatch_latency, max_memory, max_event_size, memory_partition_mode_desc,
                   CAST(track_causality AS INT) AS track_causality, CAST(startup_state AS INT) AS startup_state
              FROM sys.server_event_sessions
             WHERE name NOT IN (N'system_health', N'AlwaysOn_health', N'telemetry_xevents', N'sp_server_diagnostics session', N'hkenginexesession')
            """);
        foreach (var s in sessions)
        {
            var id = int.Parse(s["event_session_id"], CultureInfo.InvariantCulture);
            var name = s["name"];
            var fields = Rows(cmd, """
                SELECT object_id, name, CAST(value AS NVARCHAR(4000)) AS value, CAST(SQL_VARIANT_PROPERTY(value, 'BaseType') AS NVARCHAR(128)) AS base_type
                  FROM sys.server_event_session_fields WHERE event_session_id = @id ORDER BY name
                """, ("@id", id));
            string Set(string objectId)
            {
                var own = fields.Where(f => f["object_id"] == objectId).ToList();
                if (own.Count == 0) return "";
                return "SET " + string.Join(", ", own.Select(f => $"{f["name"]} = {(f["base_type"] is "nvarchar" or "varchar" or "nchar" or "char" ? N(f["value"]) : f["value"])}")) + " ";
            }

            var events = Rows(cmd, "SELECT event_id, package, name, predicate FROM sys.server_event_session_events WHERE event_session_id = @id ORDER BY package, name", ("@id", id))
                .Select(e =>
                {
                    var actions = Rows(cmd, "SELECT package, name FROM sys.server_event_session_actions WHERE event_session_id = @id AND event_id = @event ORDER BY package, name",
                        ("@id", id), ("@event", int.Parse(e["event_id"], CultureInfo.InvariantCulture))).Select(x => $"{x["package"]}.{x["name"]}").ToList();
                    var inner = Set(e["event_id"]) + (actions.Count > 0 ? $"ACTION ({string.Join(", ", actions)}) " : "") +
                                (string.IsNullOrEmpty(e["predicate"]) ? "" : $"WHERE {e["predicate"]}");
                    return $"ADD EVENT {e["package"]}.{e["name"]}" + (inner.Trim().Length > 0 ? $" ({inner.Trim()})" : "");
                });
            var targets = Rows(cmd, "SELECT target_id, package, name FROM sys.server_event_session_targets WHERE event_session_id = @id ORDER BY package, name", ("@id", id))
                .Select(t => $"ADD TARGET {t["package"]}.{t["name"]}" + (Set(t["target_id"]).Length > 0 ? $" ({Set(t["target_id"]).Trim()})" : ""));
            // The catalog keeps the latency in milliseconds and 0 for INFINITE; CREATE takes seconds.
            var latencyMs = long.Parse(s["max_dispatch_latency"], CultureInfo.InvariantCulture);
            var latency = latencyMs <= 0 ? "INFINITE" : $"{(latencyMs + 999) / 1000} SECONDS";
            var options = $"WITH (MAX_MEMORY = {s["max_memory"]} KB, EVENT_RETENTION_MODE = {s["event_retention_mode_desc"]}, MAX_DISPATCH_LATENCY = {latency}, " +
                          $"MAX_EVENT_SIZE = {s["max_event_size"]} KB, MEMORY_PARTITION_MODE = {s["memory_partition_mode_desc"]}, TRACK_CAUSALITY = {(s["track_causality"] == "1" ? "ON" : "OFF")}, " +
                          $"STARTUP_STATE = {(s["startup_state"] == "1" ? "ON" : "OFF")})";
            // Events are a comma list and targets another; no comma joins the two lists.
            var targetList = string.Join(", ", targets);
            var create = $"CREATE EVENT SESSION {Q(name)} ON SERVER {string.Join(", ", events)}{(targetList.Length > 0 ? " " + targetList : "")} {options}";
            var body = Lines(
                "    -- A session that was running is started again after it is re-created.",
                "    DECLARE @running BIT = CASE WHEN EXISTS (SELECT 1 FROM sys.dm_xe_sessions WHERE name = @name) THEN 1 ELSE 0 END;",
                $"    IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = @name) EXEC({N($"DROP EVENT SESSION {Q(name)} ON SERVER")});",
                $"    EXEC({N(create)});",
                $"    IF @running = 1 EXEC({N($"ALTER EVENT SESSION {Q(name)} ON SERVER STATE = START")});");
            yield return new ServerObjectScript("EventSessions", FileNameFor(name), IfChanged(EventSessionSignature, name, Signature(cmd, $"({EventSessionSignature})", name), body));
        }
    }
}
