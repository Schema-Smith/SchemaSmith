// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Text.RegularExpressions;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaTongs;

internal class ScriptValidator
{
    private static readonly ScriptObjectType[] GuidRenameTypes =
    [
        ScriptObjectType.Views, ScriptObjectType.Functions, ScriptObjectType.Procedures,
        ScriptObjectType.WindowFunctions, ScriptObjectType.Aggregates
    ];

    private static readonly ScriptObjectType[] ParseOnlyTypes =
    [
        ScriptObjectType.Triggers, ScriptObjectType.DDLTriggers,
        ScriptObjectType.TriggerFunctions, ScriptObjectType.Rules
    ];

    public RewriteResult RewriteWithTempName(string script, Platform platform)
    {
        var tempName = "_validate_" + Guid.NewGuid().ToString("N")[..12];
        var pattern = GetCreatePattern(platform);
        var match = pattern.Match(script);
        if (!match.Success)
            return new RewriteResult { Success = false };

        var nameGroup = match.Groups["name"];
        var quotedTemp = QuoteTempName(tempName, platform);
        var rewritten = script[..nameGroup.Index] + quotedTemp + script[(nameGroup.Index + nameGroup.Length)..];
        var kind = match.Groups["kind"].Value.ToUpperInvariant();

        // An extracted MySQL-family script opens with the real object's DROP ... IF EXISTS. Run as written during
        // validation it would drop the source's object, and no rollback brings DDL back on this family, so the DROP
        // is renamed along with the CREATE.
        if (platform.GetBasePlatform() == Platform.MySQL)
            rewritten = MySqlDropPattern.Replace(rewritten, m => m.Groups["kind"].Value.Equals(kind, StringComparison.OrdinalIgnoreCase)
                ? m.Value[..(m.Groups["name"].Index - m.Index)] + quotedTemp
                : m.Value);

        return new RewriteResult { Success = true, RewrittenScript = rewritten, TempName = tempName, ObjectKind = kind };
    }

    public string GenerateParseOnlyWrapper(string script, Platform platform)
    {
        return platform.GetBasePlatform() switch
        {
            Platform.SqlServer => $"SET PARSEONLY ON;\n{script}\nSET PARSEONLY OFF;",
            // CREATE TRIGGER is not preparable on MySQL or MariaDB, and a temporary trigger would sit live on the
            // source table while it was checked, so this family's triggers are not validated.
            Platform.MySQL => null,
            Platform.PostgreSQL => null,
            _ => throw new ArgumentException($"Unsupported platform: {platform}")
        };
    }

    public ValidationResult ValidateScript(IDbConnection connection, string script,
        ScriptObjectType objectType, Platform platform)
    {
        if (IsSkippedType(objectType))
            return new ValidationResult { IsValid = true, Skipped = true };

        string executableScript;
        string rewriteKind = null, rewriteTempName = null;
        if (Array.IndexOf(ParseOnlyTypes, objectType) >= 0)
        {
            var wrapped = GenerateParseOnlyWrapper(script, platform);
            if (wrapped == null)
                return new ValidationResult { IsValid = true, Skipped = true };
            executableScript = wrapped;
        }
        else
        {
            var rewrite = RewriteWithTempName(script, platform);
            if (!rewrite.Success)
                return new ValidationResult { IsValid = false, ErrorMessage = "Could not rewrite CREATE statement for validation" };
            executableScript = rewrite.RewrittenScript;
            rewriteKind = rewrite.ObjectKind;
            rewriteTempName = rewrite.TempName;
        }

        if (platform.GetBasePlatform() == Platform.MySQL)
            return ValidateMySqlScript(connection, executableScript, rewriteKind, rewriteTempName);

        var batches = SplitIntoBatches(executableScript, platform);

        var transaction = connection.BeginTransaction();
        try
        {
            foreach (var batch in batches)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }
            return new ValidationResult { IsValid = true };
        }
        catch (Exception ex)
        {
            return new ValidationResult { IsValid = false, ErrorMessage = ex.Message };
        }
        finally
        {
            transaction.Rollback();
        }
    }

    // No transaction on this family: DDL commits implicitly, so the temporary object is dropped explicitly, last,
    // whatever happened before. Every batch runs even after the CREATE fails, because the ones after it restore
    // the session mode the script pinned. DELIMITER blocks are split as the deploy splits them.
    private static ValidationResult ValidateMySqlScript(IDbConnection connection, string script, string kind, string tempName)
    {
        string error = null;
        try
        {
            foreach (var batch in BatchSplitter.Split(script))
            {
                try
                {
                    var command = connection.CreateCommand();
                    command.CommandText = batch;
                    command.ExecuteNonQuery();
                }
                catch (System.Data.Common.DbException ex)
                {
                    error ??= ex.Message;
                }
            }
        }
        finally
        {
            try
            {
                var drop = connection.CreateCommand();
                drop.CommandText = $"DROP {kind} IF EXISTS `{tempName}`";
                drop.ExecuteNonQuery();
            }
            catch (System.Data.Common.DbException ex)
            {
                error ??= ex.Message;
            }
        }
        return error == null ? new ValidationResult { IsValid = true } : new ValidationResult { IsValid = false, ErrorMessage = error };
    }

    private static List<string> SplitIntoBatches(string script, Platform platform)
    {
        if (platform == Platform.SqlServer)
            return SqlHelpers.SplitIntoBatches(script);
        return [script];
    }

    private static bool IsSkippedType(ScriptObjectType objectType)
    {
        return Array.IndexOf(GuidRenameTypes, objectType) < 0
            && Array.IndexOf(ParseOnlyTypes, objectType) < 0;
    }

    private static Regex GetCreatePattern(Platform platform)
    {
        // The "name" group captures the full quoted object name (including quotes) for replacement
        return platform.GetBasePlatform() switch
        {
            Platform.SqlServer => new Regex(
                @"CREATE\s+(OR\s+ALTER\s+)?(?<kind>FUNCTION|VIEW|PROCEDURE)\s+(\[\w+\]\.)?(?<name>\[\w+\]|\w+)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            Platform.PostgreSQL => new Regex(
                @"CREATE\s+(OR\s+REPLACE\s+)?(?<kind>FUNCTION|VIEW|PROCEDURE|AGGREGATE)\s+(" + @"""[\w]+""\.|[\w]+\.)?(?<name>""[\w]+""|[\w]+)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            // The engine's own text: OR REPLACE, ALGORITHM=, SQL SECURITY, AGGREGATE, JSON [RELATIONAL] DUALITY, and
            // identifiers rendered with double quotes when the routine was created under ANSI_QUOTES.
            Platform.MySQL => new Regex(
                @"CREATE\s+(OR\s+REPLACE\s+)?(ALGORITHM\s*=\s*\w+\s+)?(DEFINER\s*=\s*\S+\s+)?(SQL\s+SECURITY\s+\w+\s+)?(AGGREGATE\s+)?(JSON\s+(RELATIONAL\s+)?DUALITY\s+)?(?<kind>FUNCTION|VIEW|PROCEDURE)\s+(?<name>`[^`]+`|""[^""]+""|[\w]+)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline),
            _ => throw new ArgumentException($"Unsupported platform: {platform}")
        };
    }

    private static readonly Regex MySqlDropPattern = new(
        @"DROP\s+(?<kind>FUNCTION|VIEW|PROCEDURE)\s+(IF\s+EXISTS\s+)?(?<name>`[^`]+`|""[^""]+""|[\w]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string QuoteTempName(string tempName, Platform platform)
    {
        return platform.GetBasePlatform() switch
        {
            Platform.SqlServer => $"[{tempName}]",
            Platform.PostgreSQL => $"\"{tempName}\"",
            Platform.MySQL => $"`{tempName}`",
            _ => throw new ArgumentException($"Unsupported platform: {platform}")
        };
    }
}

internal class RewriteResult
{
    public bool Success { get; set; }
    public string RewrittenScript { get; set; }
    public string TempName { get; set; }
    public string ObjectKind { get; set; }
}

internal class ValidationResult
{
    public bool IsValid { get; set; }
    public string ErrorMessage { get; set; }
    public string ObjectName { get; set; }
    public bool Skipped { get; set; }
}
