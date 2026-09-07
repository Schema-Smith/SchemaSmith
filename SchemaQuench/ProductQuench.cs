// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using log4net;
using Microsoft.Extensions.Configuration;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Checkpointing;
using Schema.Isolators;
using Schema.Utility;
using SchemaQuench.Reporting;
using Schema.Configuration;

namespace SchemaQuench;

public class ProductQuench
{
    private readonly IConfigurationRoot _config = FactoryContainer.ResolveOrCreate<IConfigurationRoot>();
    private readonly LogHygieneOptions _logHygiene;
    private readonly ILog _errorLog = LogFactory.GetLogger("ErrorLog");
    private readonly ILog _progressLog = LogFactory.GetLogger("ProgressLog");
    private readonly Product _product = Product.Load();

    private readonly string _whatIfOnly;
    private readonly int _maxThreads;
    private readonly string _primaryServer;
    private readonly List<string> _secondaryServers = [];
    private readonly bool _runScriptsTwice;
    private readonly bool _skipKindling;
    private readonly bool _forceReKindle;
    private readonly bool _updateTables;
    private readonly bool _deliverData;
    private readonly bool _trackRunOnceMigrations;
    private readonly bool _pruneObsoleteMigrationTracking;
    private readonly bool _protectedMode;
    private readonly IReadOnlyList<string> _targetTemplates;
    private readonly IReadOnlyList<string> _targetDatabases;
    private readonly IReadOnlyList<string> _targetSchemas;
    private readonly IReadOnlyDictionary<string, TemplateTarget> _templateTargets;
    private readonly ICheckpointing _checkpointing;
    private bool _updateFailed;

    // Run-level timing collector (#243 Deployment Summary Report, slice 1). Started/stopped around
    // the whole run and handed to each DatabaseQuench work unit (see the object-initializer at the
    // DatabaseQuench construction site) so per-slot Record calls land in one shared collector for a
    // later slice to assemble into the report.
    private readonly RunTiming _runTiming = new();

    // Thread-safe collection of every migration script that actually ran this run, across both
    // template-scoped scripts (handed down to each DatabaseQuench, see the object-initializer at
    // the DatabaseQuench construction site) and product-folder scripts run directly below (#243
    // Deployment Summary Report, E4b). A later slice (E4d) maps this into the report's
    // migrationScripts[].
    private readonly MigrationScriptCapture _migrationScripts = new();

    // Thread-safe collection of every WhatIf-mode would-apply/skip/deliver entry across the run
    // (handed down to each DatabaseQuench, see the object-initializer at the DatabaseQuench
    // construction site) (#243 Deployment Summary Report, E4c). A later slice (E4d) maps this into
    // the report's whatIf section for WhatIf-mode runs.
    private readonly WhatIfCapture _whatIf = new();

    // Object-change audit collector (#243 E5). Handed to each DatabaseQuench work unit; the 4 table
    // procs write session-scoped audit rows that DatabaseQuench drains at end of work unit, and
    // object-script "ran" rows are recorded C#-side. Assembled into the report's objectChanges.
    private readonly ChangeAuditCapture _changeAudit = new();

    // Thread-safe collection of every scope failure (tenant work units today; product/server
    // phases as their capture is added), rendered once into the end-of-run roll-up.
    private readonly ConcurrentBag<FailureRecord> _failureRecords = new();
    private bool _rollupEmitted;
    private bool _anyFailure;

    // Deployment summary report (#243, E4e). _startedUtc marks the run's wall-clock start
    // (captured first thing in the QuenchProduct wrapper); _summaryWritten guards
    // WriteDeploymentSummary so an abort-path write can never double-fire alongside the
    // normal-completion write.
    private DateTime _startedUtc;
    private bool _summaryWritten;
    // Captured pre-run (before this run marks any of its own steps complete) so the report
    // reflects whether the run RESUMED — reading the checkpoint at emit time would see this
    // run's own in-progress completions and report true for nearly every run.
    private bool _resumedFromCheckpoint;

    // Thread-safe collection of every work unit's outcome + duration (#243 Deployment Summary
    // Report, slice E4a). Passive capture only — a later slice (E4d) assembles this into the
    // report's targets[]; nothing reads it yet.
    private readonly ConcurrentBag<TargetResult> _targetResults = new();

    /// <summary>Snapshot of every work unit's captured outcome so far. Used by a later slice (E4d).</summary>
    internal IReadOnlyCollection<TargetResult> TargetResults => _targetResults.ToArray();

    internal MigrationScriptCapture MigrationScripts => _migrationScripts;

    internal WhatIfCapture WhatIf => _whatIf;

    internal ChangeAuditCapture ChangeAudit => _changeAudit;

    /// <summary>
    /// True when any template or product-level step reported a fatal failure during
    /// QuenchProduct. Callers can inspect this after QuenchProduct returns to decide
    /// whether to preserve checkpoint files — a failed run must preserve checkpoints
    /// so the next invocation can resume.
    /// </summary>
    public bool Failed => _anyFailure;

    public ProductQuench()
    {
        if (_product.Platform == Platform.Unknown)
            throw new Exception($"Product '{_product.Name}' does not have a Platform assigned. Use SchemaTongs or edit the product.json file to assign a platform before quenching.");

        _logHygiene = LogHygieneOptions.FromConfiguration(_config);

        if (!int.TryParse(_config[SettingsKeys.MaxThreads], out _maxThreads) || _maxThreads < 1 || _maxThreads > 20)
            _maxThreads = 10;
        _whatIfOnly = FormatWhatIfOnly(_config[SettingsKeys.WhatIfOnly]?.ToLower() == "true");
        _runScriptsTwice = _config[SettingsKeys.RunScriptsTwice]?.ToLower() == "true";
        _primaryServer = _config[SettingsKeys.Target.Server] ?? "localhost";
        _skipKindling = _config[SettingsKeys.KindleTheForge]?.ToLower() == "false";
        // CLI-overridable (unlike the other kindling flags): ForceReKindle is an ad-hoc operational
        // gesture run on demand, not a sticky pipeline default, so a command-line switch is the natural UX.
        _forceReKindle = CommandLineParser.ContainsSwitch("ForceReKindle") || _config[SettingsKeys.ForceReKindle]?.ToLower() == "true";
        _updateTables = _config[SettingsKeys.UpdateTables]?.ToLower() != "false";
        _deliverData = _config[SettingsKeys.DeliverData]?.ToLower() != "false";
        _trackRunOnceMigrations = _config[SettingsKeys.TrackRunOnceMigrations]?.ToLower() != "false";
        _pruneObsoleteMigrationTracking = _config[SettingsKeys.PruneObsoleteMigrationTracking]?.ToLower() != "false";
        _protectedMode = ProtectedModeEnabled(_config);
        _targetTemplates = ReadFilterArray(SettingsKeys.Target.Templates);
        _targetDatabases = ReadFilterArray(SettingsKeys.Target.Databases);
        _targetSchemas = ReadFilterArray(SettingsKeys.Target.Schemas);
        _templateTargets = ReadTemplateTargets(_config);
        _checkpointing = FileCheckpointManager.GetFromFactory();

        // Secondary servers are SqlServer-only (Availability Groups)
        if (_product.Platform == Platform.SqlServer)
        {
            _secondaryServers.AddRange((_config[SettingsKeys.Target.SecondaryServers] ?? "")
                .Split([','], StringSplitOptions.RemoveEmptyEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    // Visible for testing
    internal Product LoadedProduct => _product;

    /// <summary>
    /// Reads a <c>Target.*</c> filter array from configuration. .NET configuration represents
    /// JSON arrays as keys of the form <c>Target:Templates:0</c>, <c>Target:Templates:1</c>;
    /// <c>GetSection().GetChildren()</c> enumerates those values in declaration order. Null or
    /// whitespace-only values are filtered so a stray <c>"Target:Templates:5": null</c> (carried
    /// over from a test resetting array slots) doesn't sneak into the filter. Surviving values
    /// are <c>Trim()</c>med so a user-supplied <c>" tenant_acme"</c> matches <c>"tenant_acme"</c>
    /// instead of surfacing as an unknown-name error.
    /// </summary>
    internal static IReadOnlyList<string> ReadFilterArray(IConfiguration config, string sectionKey)
    {
        var section = config.GetSection(sectionKey);
        return section.GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();
    }

    private IReadOnlyList<string> ReadFilterArray(string sectionKey) => ReadFilterArray(_config, sectionKey);

    internal static bool ResolveCascadedFlag(bool? env, bool? product, bool? template, bool defaultValue)
    {
        var effective = defaultValue;
        var locked = false;
        foreach (var level in new[] { env, product, template })
        {
            if (!level.HasValue) continue;
            if (!level.Value) { effective = false; locked = true; }
            else if (!locked) { effective = true; }
        }
        return effective;
    }

    // ResolveCascadedFlag (above) and ResolveCascadedPolicy (below) resolve their cascades by DIFFERENT
    // rules on purpose. Neither is a bug in the other; do not "fix" one to match.
    //
    //   ResolveCascadedFlag  — veto / AND. A false at ANY level locks the result false and the levels
    //                          COMBINE into a single answer.
    //   ResolveCascadedPolicy — most-specific-wins on the WHOLE object. The nearest non-null level is
    //                          returned entire and no other level is consulted at all.
    //
    // So a table declaring only `Mode: ALWAYS` does NOT pick up the product's `Threshold` — it gets its
    // own policy, whole. Blending the levels field-by-field would hand a partially-specified table a
    // trigger its author never wrote, which is the opposite of what a per-table override is for.
    internal static RebuildPolicy ResolveCascadedPolicy(RebuildPolicy env, RebuildPolicy product, RebuildPolicy template, RebuildPolicy table) =>
        table ?? template ?? product ?? env ?? new RebuildPolicy();

    /// <summary>
    /// Composes the environment tier's <see cref="RebuildPolicy"/> from its three flat settings keys.
    /// Returns null when none of them is set, so an unconfigured environment stays absent from the
    /// cascade rather than shadowing a product/template policy with an all-defaults object.
    /// </summary>
    internal static RebuildPolicy ReadEnvRebuildPolicy(IConfiguration config)
    {
        var mode = config[SettingsKeys.RebuildPolicyMode];
        var threshold = config[SettingsKeys.RebuildPolicyThreshold];
        var onOrderMismatch = ConfigBool(config, SettingsKeys.RebuildPolicyOnOrderMismatch);

        if (string.IsNullOrWhiteSpace(mode) && string.IsNullOrWhiteSpace(threshold) && !onOrderMismatch.HasValue)
            return null;

        var policy = new RebuildPolicy { OnOrderMismatch = onOrderMismatch == true };
        if (!string.IsNullOrWhiteSpace(mode)) policy.Mode = mode.Trim().ToUpperInvariant();
        if (int.TryParse(threshold, out var parsedThreshold)) policy.Threshold = parsedThreshold;
        return policy;
    }

    internal static bool? ConfigBool(IConfiguration config, string key)
    {
        var raw = config[key];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return bool.TryParse(raw, out var parsed) ? parsed : (bool?)null;
    }

    // Env-level no-drop protection tier (#270 Slice E). When PreventDrop is true the environment
    // never drops an object BY ABSENCE: every Drop…RemovedFromProduct pass (plus DropUnknownIndexes)
    // is suppressed, each skipped drop is recorded to the ChangeAudit seam and surfaced as a
    // PreventDropSummary manifest, and the run completes normally (exit 0). Transient drop-then-
    // recreate that is part of applying a declared change is unaffected — protected mode touches
    // only the by-absence lever.
    internal static bool ProtectedModeEnabled(IConfiguration config) => ConfigBool(config, SettingsKeys.PreventDrop) == true;

    /// <summary>
    /// Reads <c>Target.TemplateTargets</c> from configuration into the per-template override map.
    /// Each template name becomes a dictionary key (case-insensitive) carrying the parsed
    /// <see cref="TemplateTarget.Databases"/> / <see cref="TemplateTarget.Schemas"/> lists and
    /// the <see cref="TemplateTarget.CreateIfMissing"/> flag. Missing section returns an empty
    /// dictionary so callers can do an unconditional lookup without null-guarding. Whitespace-only
    /// values inside the Databases / Schemas arrays are dropped and surviving values are trimmed
    /// (mirrors the discipline applied by <see cref="ReadFilterArray(IConfiguration, string)"/>).
    /// </summary>
    internal static IReadOnlyDictionary<string, TemplateTarget> ReadTemplateTargets(IConfiguration config)
    {
        var result = new Dictionary<string, TemplateTarget>(StringComparer.OrdinalIgnoreCase);
        var section = config.GetSection(SettingsKeys.Target.TemplateTargets);
        foreach (var child in section.GetChildren())
        {
            var templateName = child.Key;
            var createIfMissingSection = child.GetSection(SettingsKeys.TemplateTarget.CreateIfMissing);
            var createIfMissingRaw = createIfMissingSection.Value;
            var target = new TemplateTarget
            {
                Databases = child.GetSection(SettingsKeys.TemplateTarget.Databases).GetChildren()
                    .Select(c => c.Value)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!.Trim())
                    .ToList(),
                Schemas = child.GetSection(SettingsKeys.TemplateTarget.Schemas).GetChildren()
                    .Select(c => c.Value)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!.Trim())
                    .ToList(),
                CreateIfMissing = bool.TryParse(createIfMissingRaw, out var c) && c
            };
            // Skip entries that are entirely empty AND have no explicit CreateIfMissing key.
            // .NET configuration providers retain a previously-set key path even after the
            // values are nulled (the in-memory provider used by Microsoft.Extensions.Configuration
            // can leave phantom entries after a test fixture clears state), so iteration may
            // surface a `Foo: {}` after cleanup. Detect "key genuinely absent" — distinct from
            // "key present with empty value" — so a user-authored `CreateIfMissing: ""` reaches
            // the validator and trips rule 3 instead of being silently swallowed by this skip.
            var createIfMissingKeyPresent =
                createIfMissingSection.Value != null || createIfMissingSection.GetChildren().Any();
            if (target.HasNoTargets && !createIfMissingKeyPresent)
                continue;
            result[templateName] = target;
        }
        return result;
    }

    /// <summary>
    /// Returns the init database name used for server-level connections per platform.
    /// </summary>
    internal static string GetInitDatabase(Platform platform) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => "master",
        Platform.PostgreSQL => "postgres",
        Platform.MySQL => "information_schema",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, $"Unsupported platform: {platform}")
    };

    /// <summary>
    /// Returns the server identification query per platform.
    /// </summary>
    internal static string GetServerIdQuery(Platform platform) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => "SELECT @@SERVERNAME",
        Platform.PostgreSQL => "SELECT inet_server_addr();",
        Platform.MySQL => "SELECT @@hostname",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, $"Unsupported platform: {platform}")
    };

    /// <summary>
    /// Formats the WhatIfOnly value per platform convention.
    /// SqlServer/MySQL use "1"/"0"; PostgreSQL uses "true"/"false".
    /// </summary>
    internal string FormatWhatIfOnly(bool isWhatIf) => _product.Platform switch
    {
        Platform.PostgreSQL => isWhatIf ? "true" : "false",
        _ => isWhatIf ? "1" : "0"
    };

    /// <summary>
    /// Formats boolean flags per platform convention.
    /// SqlServer/MySQL use "1"/"0"; PostgreSQL uses "true"/"false".
    /// </summary>
    internal string FormatBooleanFlag(bool value) => _product.Platform switch
    {
        Platform.PostgreSQL => value ? "true" : "false",
        _ => value ? "1" : "0"
    };

    /// <summary>
    /// Converts a scalar result to boolean.
    /// MySQL returns Int64 (0/1) from EXISTS(), SQL Server returns bool, PostgreSQL returns bool.
    /// Delegates to the shared <see cref="Schema.Delivery.GateEvaluator"/>.
    /// </summary>
    internal static bool ScalarToBool(object result) => Schema.Delivery.GateEvaluator.ScalarToBool(result);

    internal static List<string> BuildMinimumVersionFailures(
        IEnumerable<(string Server, TargetVersionInfo Info)> detected,
        int requiredComparable,
        string declaredMinimum)
    {
        return detected
            .Where(d => !VersionHelper.IsAtLeast(d.Info.ServerComparable, requiredComparable))
            .Select(d => $"  {d.Server}: detected version {VersionHelper.DisplayVersion(d.Info)} is below the product's declared MinimumVersion {declaredMinimum}")
            .ToList();
    }

    /// <summary>
    /// Enforces SchemaSmith's intrinsic per-engine version floor on every target server, independent
    /// of the opt-in <c>Product.MinimumVersion</c> guardrail, and logs each server's detected version.
    /// Fails fast with a clear "unsupported version" message before any kindling — so a below-floor
    /// server (e.g. SQL Server 2012, where the engine scripts' STRING_AGG would otherwise die with a
    /// raw "not a recognized built-in function" error) is caught up front.
    /// </summary>
    internal void ValidateServerVersionFloor()
    {
        _progressLog.Info("Validate server version floor");
        foreach (var server in _secondaryServers.Union([_primaryServer]))
        {
            using var command = GetCommand(server);
            try
            {
                var info = TargetVersionDetector.Detect(command, _product.Platform);
                _progressLog.Info($"  {server}: detected {info.Platform} version {VersionHelper.DisplayVersion(info)}");
                PreFlightVersionGuard.CheckOrThrow(info, server);
            }
            finally
            {
                command.Connection?.Close();
                command.Connection?.Dispose();
            }
        }
    }

    internal void ValidateMinimumVersion()
    {
        if (string.IsNullOrWhiteSpace(_product.MinimumVersion))
            return;

        var required = VersionHelper.ParseDeclaredVersion(_product.MinimumVersion, _product.Platform);
        if (required == null)
            throw new Exception($"Product MinimumVersion '{_product.MinimumVersion}' is not a valid {_product.Platform} version.");

        _progressLog.Info("Validate Minimum Version");
        var detected = new List<(string Server, TargetVersionInfo Info)>();
        foreach (var server in _secondaryServers.Union([_primaryServer]))
        {
            using var command = GetCommand(server);
            try
            {
                detected.Add((server, TargetVersionDetector.Detect(command, _product.Platform)));
            }
            finally
            {
                command.Connection?.Close();
                command.Connection?.Dispose();
            }
        }

        var failures = BuildMinimumVersionFailures(detected, required.Value, _product.MinimumVersion);
        if (failures.Count > 0)
            throw new Exception(
                "One or more target servers are below the product's declared MinimumVersion; aborting before any deployment:"
                + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Filters product folders by their <c>ShouldApplyExpression</c> evaluated against the server.
    /// Blank expressions always apply; a false expression drops the folder (logged). Evaluation
    /// errors propagate so the run fails closed rather than silently skipping a folder.
    /// Product-folder gates run before any database is selected (the third gate site, B6b, aligned
    /// with the folder/delivery gates' N2 fix) — <see cref="ResolveProductScopeGateTokens"/> resolves
    /// only what is meaningful at that scope.
    /// </summary>
    internal List<ProductFolder> GateProductFolders(IDbCommand command, IEnumerable<ProductFolder> folders, string server = null)
    {
        var folderList = folders as IReadOnlyCollection<ProductFolder> ?? folders.ToList();
        var tokens = ResolveProductScopeGateTokens(command, folderList);

        var survivors = new List<ProductFolder>();
        foreach (var folder in folderList)
        {
            var expression = tokens.Count == 0
                ? folder.ShouldApplyExpression
                : SqlScript.TokenReplace(folder.ShouldApplyExpression, tokens, _product.Platform);
            try
            {
                if (!FolderGate.ShouldApply(command, expression))
                {
                    _progressLog.Info($"  Skipping folder '{folder.FolderPath}'{ServerSuffix(server)} — ShouldApplyExpression evaluated false");
                    continue;
                }
            }
            catch (Exception e)
            {
                var message = $"Folder '{folder.FolderPath}'{ServerSuffix(server)} ShouldApplyExpression failed: {e.Message}";
                _progressLog.Error($"  {message}");
                _errorLog.Error(message, e);
                throw;
            }

            survivors.Add(folder);
        }

        return survivors;
    }

    // {{ServerMajorVersion}} placeholder text, matched the same way DatabaseQuench's ContainsVersionToken
    // checks for a version token before paying to resolve it.
    private const string ServerMajorVersionPlaceholder = "{{ServerMajorVersion}}";

    /// <summary>
    /// Product-folder gates run before any database is selected, so <c>{{ServerMajorVersion}}</c> is the
    /// only gate token resolvable here (the server connection is already open); <c>{{CompatibilityLevel}}</c>
    /// and <c>{{SchemaName}}</c> stay out of scope — see <see cref="TokenHelper.AssembleProductScopeGateTokens"/>.
    /// Detects the version only when some folder's expression actually references it, so a run with no
    /// version-gated product folder pays no extra round trip beyond the gate evaluations it was already doing.
    /// </summary>
    private List<KeyValuePair<string, string>> ResolveProductScopeGateTokens(IDbCommand command, IReadOnlyCollection<ProductFolder> folders)
    {
        var needsVersion = folders.Any(f =>
            !string.IsNullOrWhiteSpace(f.ShouldApplyExpression) &&
            f.ShouldApplyExpression.Contains(ServerMajorVersionPlaceholder, StringComparison.OrdinalIgnoreCase));
        if (!needsVersion) return [];

        var versionInfo = TargetVersionDetector.Detect(command, _product.Platform);
        return TokenHelper.AssembleProductScopeGateTokens(versionInfo.ServerComparable);
    }

    private static string ServerSuffix(string server) => string.IsNullOrEmpty(server) ? "" : $" on {server}";

    /// <summary>
    /// Returns the active WhatIfOnly value (for testing visibility).
    /// </summary>
    internal bool IsWhatIfOnly => _product.Platform == Platform.PostgreSQL
        ? _whatIfOnly == "true"
        : _whatIfOnly == "1";

    private TrackingScope ProductScope => new TrackingScope { ProductName = _product.Name };

    private TrackingScope ProductScopeForServer(string server) => new TrackingScope
    {
        ProductName = _product.Name,
        Server = server
    };

    internal virtual IDbCommand GetCommand(string server)
    {
        var initDb = GetInitDatabase(_product.Platform);
        var connectionStringOverride = CommandLineParser.ValueOfSwitch("ConnectionString", null);
        string connectionString;
        if (!string.IsNullOrEmpty(connectionStringOverride) && server == _primaryServer)
        {
            if (_product.Platform.GetBasePlatform() == Platform.MySQL &&
                !connectionStringOverride.Contains("AllowUserVariables", StringComparison.OrdinalIgnoreCase))
            {
                LogFactory.GetLogger("ProgressLog").Warn("Connection string override for MySQL does not contain AllowUserVariables=true. " +
                                                         "This is required for SchemaSmith stored procedures that use PREPARE/EXECUTE.");
            }
            connectionString = connectionStringOverride;
        }
        else
        {
            connectionString = TargetConnectionString.Build(_product.Platform, server, initDb, _config);
        }
        var factory = DbConnectionFactory.ForPlatform(_product.Platform);
        var connection = factory.GetDbConnection(connectionString);
        try
        {
            connection.Open();
        }
        catch (Exception e)
        {
            throw new Exception($"Unable to connect to {server}{(!string.IsNullOrWhiteSpace(_config[SettingsKeys.Target.User]) ? $" with user {_config[SettingsKeys.Target.User]}" : "")}", e);
        }
        var command = connection.CreateCommand();
        command.CommandTimeout = 0;

        return command;
    }

    /// <summary>
    /// Resolves <see cref="Template.IdentificationDatabase"/> through the product + template
    /// non-query token set (so a <c>{{ControlDb}}</c>-style token gives per-environment control),
    /// returning null/empty when unset (⇒ enumeration uses the platform init database).
    /// </summary>
    internal string ResolveIdentificationDatabase(Template template)
    {
        if (string.IsNullOrWhiteSpace(template.IdentificationDatabase)) return null;
        var tokens = _product.NonQueryTokens.Concat(template.NonQueryTokens).ToList();
        return SqlScript.TokenReplace(template.IdentificationDatabase, tokens, _product.Platform);
    }

    /// <summary>
    /// Opens the command used to run <see cref="Template.DatabaseIdentificationScript"/> for
    /// enumeration. When the template sets <see cref="Template.IdentificationDatabase"/>, the
    /// connection targets that database (e.g. a control-plane registry DB) instead of the platform
    /// init database — the only way a PostgreSQL fleet can read a registry table at enumeration
    /// time. A <c>--ConnectionString</c> override on the primary server is re-targeted to the
    /// identification database (mirroring <see cref="ComposeAdminDbConnectionString"/>). When unset,
    /// this is exactly <see cref="GetCommand"/> (init DB) — today's behavior. Provisioning and
    /// existence checks are NOT affected; they stay on the init database via
    /// <see cref="GetCommandForAdminDb"/>.
    /// </summary>
    internal virtual IDbCommand GetCommandForIdentification(string server, Template template)
    {
        if (string.IsNullOrWhiteSpace(ResolveIdentificationDatabase(template)))
            return GetCommand(server);

        var connectionString = ComposeIdentificationConnectionString(server, template);
        var factory = DbConnectionFactory.ForPlatform(_product.Platform);
        var connection = factory.GetDbConnection(connectionString);
        try
        {
            connection.Open();
        }
        catch (Exception e)
        {
            throw new Exception($"Unable to connect to {server} [IdentificationDatabase: {ResolveIdentificationDatabase(template)}]{(!string.IsNullOrWhiteSpace(_config[SettingsKeys.Target.User]) ? $" with user {_config[SettingsKeys.Target.User]}" : "")}", e);
        }
        var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        return command;
    }

    /// <summary>
    /// Composes the connection string for the enumeration command when
    /// <see cref="Template.IdentificationDatabase"/> is set — targeting that (token-resolved)
    /// database. A <c>--ConnectionString</c> override on the primary server is re-targeted to it via
    /// <see cref="ConnectionString.RetargetDatabase"/>; otherwise the string is built from
    /// <c>Target:*</c> config with the identification database substituted for the init database.
    /// Extracted as a seam so the composition is unit-testable without opening a connection.
    /// </summary>
    internal virtual string ComposeIdentificationConnectionString(string server, Template template)
    {
        var identificationDb = ResolveIdentificationDatabase(template);
        var connectionStringOverride = CommandLineParser.ValueOfSwitch("ConnectionString", null);
        if (!string.IsNullOrEmpty(connectionStringOverride) && server == _primaryServer)
            return ConnectionString.RetargetDatabase(connectionStringOverride, identificationDb, _product.Platform);

        return TargetConnectionString.Build(_product.Platform, server, identificationDb, _config);
    }

    /// <summary>
    /// Opens an admin-database command against <paramref name="server"/> for DB-axis provisioning.
    /// When a <c>--ConnectionString</c> override is in effect AND the server is the primary server,
    /// the override is re-targeted to the platform's init database
    /// (<c>master</c> / <c>postgres</c> / <c>information_schema</c>) via
    /// <see cref="ConnectionString.RetargetDatabase"/> — distinct from <see cref="GetCommand"/>,
    /// which honors the override's <c>Database=</c> verbatim. This matters because
    /// <see cref="DatabaseExistsOnServer"/> and <see cref="ProvisionDatabaseViaAdminConnection"/>
    /// MUST run against the admin DB: PostgreSQL's <c>CREATE DATABASE</c> cannot run inside a
    /// transaction AND cannot target a non-admin DB; SQL Server / MySQL happen to tolerate the
    /// override's target DB but the admin-DB target is the correct contract across all engines.
    /// </summary>
    internal virtual IDbCommand GetCommandForAdminDb(string server)
    {
        var connectionString = ComposeAdminDbConnectionString(server);
        var factory = DbConnectionFactory.ForPlatform(_product.Platform);
        var connection = factory.GetDbConnection(connectionString);
        try
        {
            connection.Open();
        }
        catch (Exception e)
        {
            throw new Exception($"Unable to connect to {server}{(!string.IsNullOrWhiteSpace(_config[SettingsKeys.Target.User]) ? $" with user {_config[SettingsKeys.Target.User]}" : "")}", e);
        }
        var command = connection.CreateCommand();
        command.CommandTimeout = 0;

        return command;
    }

    /// <summary>
    /// Composes the admin-database connection string used by <see cref="GetCommandForAdminDb"/>.
    /// Split out as an internal virtual seam so tests can verify the re-target logic — the
    /// <c>--ConnectionString</c> override path AND the configured-target path — without standing
    /// up a live DB connection.
    /// </summary>
    internal virtual string ComposeAdminDbConnectionString(string server)
    {
        var initDb = GetInitDatabase(_product.Platform);
        var connectionStringOverride = CommandLineParser.ValueOfSwitch("ConnectionString", null);
        if (!string.IsNullOrEmpty(connectionStringOverride) && server == _primaryServer)
        {
            if (_product.Platform.GetBasePlatform() == Platform.MySQL &&
                !connectionStringOverride.Contains("AllowUserVariables", StringComparison.OrdinalIgnoreCase))
            {
                LogFactory.GetLogger("ProgressLog").Warn("Connection string override for MySQL does not contain AllowUserVariables=true. " +
                                                         "This is required for SchemaSmith stored procedures that use PREPARE/EXECUTE.");
            }
            return ConnectionString.RetargetDatabase(connectionStringOverride, initDb, _product.Platform);
        }
        return TargetConnectionString.Build(_product.Platform, server, initDb, _config);
    }

    public bool RunPreFlight(bool previewTargets)
    {
        _progressLog.Info($"Pre-flight diagnostics for {_product.Name} ({(previewTargets ? "--PreviewTargets" : "--TestConnection")})");
        try
        {
            TestServerConnections();      // throws on failure
            ValidateServerVersionFloor(); // intrinsic engine floor — throws below floor
            ValidateMinimumVersion();     // opt-in product floor — throws on failure
        }
        catch (Exception e)
        {
            _progressLog.Error($"Pre-flight FAILED: {e.Message}");
            _anyFailure = true;
            return false;
        }

        if (!previewTargets)
        {
            _progressLog.Info("RESULT: PASS (connections and minimum version validated)");
            return true;
        }

        return PreviewTargets();   // implemented in Task 3
    }

    private bool PreviewTargets()
    {
        var templates = LoadTemplates();
        TemplateTargetValidator.Validate(_templateTargets, templates, _targetTemplates);
        if (!TryFilterTemplatesByTarget(templates, out var inScope)) return true;

        var previews = new List<TemplatePreview>();
        foreach (var template in inScope)
        {
            if (string.IsNullOrWhiteSpace(template.DatabaseIdentificationScript)) continue;
            var units = EnumerateWorkUnitsForTemplate(template, previewOnly: true);
            try
            {
                units = ApplyPerTemplateTargetFilter(template, units);
            }
            catch (InvalidOperationException ex)
            {
                _progressLog.Error($"Target filter rejection for template '{template.Name}': {ex.Message}");
                _errorLog.Error($"Target filter rejection for template '{template.Name}': {ex.Message}");
                _anyFailure = true;
                previews.Add(new TemplatePreview(template.Name, template.RequireAtLeastOneTarget, Array.Empty<WorkUnit>(), matchedNothing: true));
                continue;
            }
            var matchedNothing = template.RequireAtLeastOneTarget && units.Count == 0;
            previews.Add(new TemplatePreview(template.Name, template.RequireAtLeastOneTarget, units, matchedNothing));
        }

        foreach (var line in PreFlightReporter.Render(previews)) _progressLog.Info(line);

        var anyRequiredMissed = previews.Any(p => p.matchedNothing);
        if (anyRequiredMissed)
        {
            _anyFailure = true;
            _progressLog.Error("RESULT: FAIL (one or more required templates matched nothing)");
            return false;
        }
        _progressLog.Info("RESULT: PASS");
        return true;
    }

    /// <summary>
    /// Applies the <c>Target.Databases</c> / <c>Target.Schemas</c> per-template filter to
    /// <paramref name="units"/>. Returns the filtered list unchanged when the filter is empty
    /// or the input is empty. Throws <see cref="InvalidOperationException"/> on rejection
    /// (unknown filter value or zero-result intersection) — callers handle the exception
    /// differently: <see cref="QuenchTemplate"/> logs + exits; <see cref="PreviewTargets"/>
    /// logs + continues so remaining templates are still reported.
    /// </summary>
    private List<WorkUnit> ApplyPerTemplateTargetFilter(Template template, List<WorkUnit> units)
    {
        if (units.Count == 0 || (_targetDatabases.Count == 0 && _targetSchemas.Count == 0))
            return units;

        var perTemplateFilter = new WorkUnitFilter([], _targetDatabases, _targetSchemas);
        var filtered = perTemplateFilter.Apply(units, _progressLog.Warn);
        _progressLog.Info($"[Target] Resolved {filtered.Count} work unit(s) after filtering {units.Count} discovered unit(s) for template '{template.Name}'.");
        return filtered;
    }

    public void QuenchProduct(bool suppressKindlingForTesting = false)
    {
        _startedUtc = DateTime.UtcNow;

        _runTiming.Start();
        try
        {
            QuenchProductCore(suppressKindlingForTesting);
        }
        catch
        {
            // An exception escaping Core (e.g. a baseline/validate/after-product-script failure that
            // throws rather than routing through a hard-abort site) heads for the unhandled-exception
            // handler, which backs up logs and exits 3. Emit a best-effort Aborted summary first so
            // even these failure exits produce a report, then rethrow to preserve existing behavior.
            _runTiming.Stop();
            WriteDeploymentSummary(RunOutcome.Aborted, 3);
            throw;
        }
        _runTiming.Stop();

        // Normal Success / continue-mode PartialFailure. (Hard-abort sites inside Core already wrote
        // their Aborted summary and Exit()ed before control could return here.)
        WriteDeploymentSummary(Failed ? RunOutcome.PartialFailure : RunOutcome.Success, Failed ? 2 : 0);
    }

    private void QuenchProductCore(bool suppressKindlingForTesting)
    {
        _progressLog.Info($"Begin Quench of {_product.Name}");

        if (_protectedMode)
            _progressLog.Info("PreventDrop is ENABLED — this environment will skip every drop-by-absence and report the suppressed drops (no objects are dropped for being absent from the product).");
        else if (!string.IsNullOrWhiteSpace(_config[SettingsKeys.PreventDrop]) && ConfigBool(_config, SettingsKeys.PreventDrop) == null)
            _progressLog.Warn($"PreventDrop value '{_config[SettingsKeys.PreventDrop]}' is not a recognized boolean (true/false) — the no-drop protection tier is NOT enabled.");

        LogProductInfo();

        TestServerConnections();

        ValidateServerVersionFloor();

        ValidateMinimumVersion();

        RemoveOldTableQuenchScripts();

        var summary = _checkpointing.GetProductCheckpointSummary(_product.Name);
        _resumedFromCheckpoint = summary.HasAnyCompleted;
        if (summary.HasAnyCompleted)
            _progressLog.Info($"Resuming from checkpoint (Before Scripts: {summary.TotalBeforeScripts} across {summary.ServersWithBeforeScripts} server(s), Templates: {summary.CompletedTemplates}, After Scripts: {summary.TotalAfterScripts} across {summary.ServersWithAfterScripts} server(s))");

        using var command = GetCommand(_primaryServer);

        try
        {
            if (!string.IsNullOrWhiteSpace(_product.ValidationScript))
            {
                _progressLog.Info("Validate Server");
                command.CommandText = _product.ValidationScript;
                if (!ScalarToBool(command.ExecuteScalar()))
                {
                    _failureRecords.Add(new FailureRecord("Validate", "Product",
                        "Invalid server for this product", Array.Empty<string>(), null));
                    EmitFailureRollup();
                    throw new Exception("Invalid server for this product");
                }
            }

            if (!string.IsNullOrWhiteSpace(_product.BaselineValidationScript))
            {
                _progressLog.Info("Validate Baseline");
                command.CommandText = _product.BaselineValidationScript;
                if (!ScalarToBool(command.ExecuteScalar()))
                {
                    _failureRecords.Add(new FailureRecord("Validate", "Product",
                        "Invalid baseline for this release", Array.Empty<string>(), null));
                    EmitFailureRollup();
                    throw new Exception("Invalid baseline for this release");
                }
            }

            if (_product.QueryTokens.Count > 0)
            {
                _progressLog.Info("Resolving Product Level Query Tokens");
                TokenHelper.ResolveQueryTokens(_product.QueryTokens, _product.NonQueryTokens.ToList(), command, Path.GetDirectoryName(_product.FilePath), _product.Platform);
                foreach (var script in _product.ScriptFolders.SelectMany(p => p.Scripts))
                    script.ReplaceQueryTokens(_product.QueryTokens.ToList());
            }

            QuenchProductScriptsWithCheckpoint(_product.BeforeFolders, "Before Product", true);

            var templates = LoadTemplates();
            var suppressKindling = suppressKindlingForTesting || _skipKindling;

            // TemplateTargets pre-flight: validate Target.TemplateTargets against the loaded
            // template list AND the active Target.Templates filter BEFORE any identification
            // scripts run. Rule 2 (filter-excluded template) is checked against the RAW
            // _targetTemplates filter list — not the filtered set — so a config that targets a
            // template the filter would exclude surfaces a precise diagnostic instead of a
            // misleading "unknown template" later. Throwing here aborts the run with the
            // user-facing message bubbled up; the existing finally clause around QuenchProduct's
            // command disposes the primary-server connection.
            TemplateTargetValidator.Validate(_templateTargets, templates, _targetTemplates);

            // Slice-5 selective execution (§9.3): filter the templates list by Target.Templates
            // before enumeration; the excluded templates' DatabaseIdentificationScripts never run
            // (no point validating against a universe we're not touching). Filter-value validation
            // for Target.Templates runs against the loaded template list — typos surface here.
            if (!TryFilterTemplatesByTarget(templates, out var templatesInScope))
                return;

            foreach (var template in templatesInScope)
            {
                var stepName = $"Template:{template.Name}";
                if (_checkpointing.HasCompleted(ProductScope, stepName))
                {
                    _progressLog.Info($"Skipping template '{template.Name}' (previously completed per checkpoint)");
                    continue;
                }
                // Explicit check-then-mark rather than Track(): QuenchTemplate handles its own
                // failure via LogBackup.BackupLogsAndExit(2), which terminates the process in
                // production but is a mocked no-op under tests. Using Track() would interpret the
                // non-throwing return as success and record the template as complete even when
                // _updateFailed is set, leading to the template being skipped on the next run.
                QuenchTemplate(template, suppressKindling);
                if (!_updateFailed)
                {
                    _checkpointing.MarkStepCompleted(ProductScope, stepName);
                }
                else if (ShouldAbortOnFailure(template))
                {
                    // Abort mode: BackupLogsAndExit(2) was called. In production, the process
                    // terminates. In tests (mocked exit), we break here to preserve the
                    // "subsequent templates do not run" guarantee. Must mirror the abort-gate
                    // logic in QuenchTemplate so the two sites stay in sync.
                    break;
                }
            }

            QuenchProductScriptsWithCheckpoint(_product.AfterFolders, "After Product", false);

            if (!string.IsNullOrWhiteSpace(_product.VersionStampScript))
            {
                _progressLog.Info("Stamp product version");
                command.CommandText = _product.VersionStampScript;
                command.ExecuteNonQuery();
            }
        }
        finally
        {
            command.Connection?.Close();
            command.Connection?.Dispose();
        }

        _progressLog.Info($"Completed quench of {_product.Name}");
        EmitFailureRollup();
    }

    /// <summary>
    /// Renders the collected <see cref="FailureRecord"/>s into the phase-grouped end-of-run roll-up
    /// and writes it to the FailureLog logger (<c>SchemaQuench - Failures.log</c>) + echoes it to the
    /// progress console. Idempotent and a no-op on a clean run (empty render — zero added noise).
    /// Called at normal completion and immediately before each abort-path log backup + exit.
    /// </summary>
    internal void EmitFailureRollup()
    {
        if (_rollupEmitted) return;
        _rollupEmitted = true;
        var report = FailureReportWriter.Render(_failureRecords.ToArray());
        if (string.IsNullOrEmpty(report)) return;
        // Roll-up goes to the FailureLog logger (SchemaQuench - Failures.log + its own console
        // appender) — NOT through _progressLog — so it reaches the console once at end of run without
        // duplicating error content into the progress stream that exact-count assertions depend on.
        LogFactory.GetLogger("FailureLog").Info(report);
    }

    /// <summary>
    /// Assembles and writes the deployment summary report (#243, E4e) — the run's normal
    /// Success/PartialFailure completion AND all three hard-abort sites funnel through here.
    /// <see cref="_summaryWritten"/> makes this idempotent (an abort path writes once, then
    /// <c>LogBackup.BackupLogsAndExit</c> terminates the process) and the try/catch makes it
    /// strictly best-effort: a failure to assemble or write the report is logged as a warning
    /// and swallowed — it must never disrupt the run's real logging, exit code, or control flow.
    /// </summary>
    private void WriteDeploymentSummary(RunOutcome outcome, int exitCode)
    {
        if (_summaryWritten) return;
        _summaryWritten = true;
        try
        {
            var mode = IsWhatIfOnly ? RunMode.WhatIf : RunMode.Quench;
            var resumed = _resumedFromCheckpoint;
            var summary = DeploymentSummaryAssembler.Assemble(
                _product.Name, _product.Platform.ToString(), ToolVersion(),
                _startedUtc, DateTime.UtcNow, mode, outcome, exitCode, resumed,
                _targetResults.ToArray(), _runTiming, _migrationScripts.Snapshot(),
                _whatIf.Snapshot(), _failureRecords.ToArray(), BottleneckThresholdMs(), _changeAudit,
                protectedModeEnabled: _protectedMode);
            var (jsonPath, mdPath) = ResolveReportPaths(CommandLineParser.ValueOfSwitch("report", null), ConfigHelper.ResolveLogPath(), "SchemaQuench");
            EnsureReportDirectory(jsonPath);
            var file = FileWrapper.GetFromFactory();
            file.WriteAllText(jsonPath, DeploymentSummaryJson.Serialize(summary));
            file.WriteAllText(mdPath, DeploymentSummaryText.Render(summary));
            _progressLog.Info($"Deployment summary written: {jsonPath}");
        }
        catch (Exception ex)
        {
            // Best-effort: a summary-write failure must never disrupt the run's real logging or exit.
            _progressLog.Warn($"Could not write deployment summary: {ex.Message}");
        }
    }

    /// <summary>
    /// Unit-testable path resolution: <c>--report &lt;path&gt;</c> redirects both report files to
    /// <c>&lt;path&gt;.json</c> / <c>&lt;path&gt;.md</c>; otherwise both default alongside the logs
    /// in <paramref name="logDir"/> as <c>{appName} - Summary.json</c> / <c>.md</c>.
    /// </summary>
    /// Creates the parent directory of a resolved report path if it doesn't exist, so a
    /// <c>--report &lt;path&gt;</c> pointing at a not-yet-created directory writes the summary
    /// instead of silently failing (the default log-dir path already exists). A no-op when the
    /// path has no directory component (bare filename) or the directory is already present.
    internal static void EnsureReportDirectory(string reportFilePath)
    {
        var dir = Path.GetDirectoryName(reportFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    internal static (string JsonPath, string MdPath) ResolveReportPaths(string reportSwitch, string logDir, string appName) =>
        string.IsNullOrWhiteSpace(reportSwitch)
            ? (Path.Join(logDir, $"{appName} - Summary.json"), Path.Join(logDir, $"{appName} - Summary.md"))
            : (reportSwitch + ".json", reportSwitch + ".md");

    // Relativizes a product-folder script's on-disk path against the product root for the
    // deployment summary, so product scripts read like "Jobs/Job 1.sql" rather than a full
    // absolute path — matching the relative form DatabaseQuench.GetRelativeScriptPath produces
    // for template scripts.
    internal static string MakeScriptPathRelative(string filePath, string baseDir)
    {
        var stripped = LongPathSupport.StripLongPathPrefix(filePath);
        if (!string.IsNullOrEmpty(baseDir)) stripped = stripped.Replace(baseDir, "");
        return stripped
            .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .TrimStart(Path.AltDirectorySeparatorChar);
    }

    private int BottleneckThresholdMs() =>
        int.TryParse(_config[SettingsKeys.BottleneckThresholdMs], out var ms) && ms >= 0 ? ms : 30000;

    // Mirrors CommandLineParser.ShowVersionAndExit's reflection so the deployment summary
    // reports the same tool version the CLI's --version switch shows.
    private static string ToolVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
               ?? assembly.GetName().Version?.ToString() ?? "unknown";
    }

    internal static readonly string[] SpecialTokenTags = ["TableSchema_", "ObjectScripts_", "QueryTokens_", "MaterializedViewSchema_", "IndexedViewSchema_",
        "TableXml_", "MaterializedViewXml_", "IndexedViewXml_"];

    /// <summary>
    /// Cross-template placeholder for the per-iteration <c>{{SchemaName}}</c> token (audit I8).
    /// Snapshots of a schema template's TableSchema / MaterializedViewSchema / IndexedViewSchema
    /// surface in OTHER templates as <c>{{TableSchema_&lt;SchemaTemplate&gt;}}</c> tokens. Those
    /// consuming templates don't re-run per-iteration substitution, so a literal
    /// <c>{{SchemaName}}</c> would leak into the consumer's runtime SQL. Replacing it with this
    /// visible placeholder preserves introspection (users see "this is iteration-dependent")
    /// while avoiding the corruption. Documented in
    /// <c>docs/end-user/reference/script-tokens.md</c>.
    /// </summary>
    internal const string CrossTemplateSchemaPlaceholder = "<per-iteration>";

    internal static Dictionary<string, string> BuildSpecialTokens(Template template)
    {
        // Schema-template content carries the {{SchemaName}} token until iteration time.
        // When a regular (or other) template embeds this snapshot via {{TableSchema_<Name>}},
        // the literal token would never be resolved at runtime — replace it with a visible
        // placeholder so the cross-template surface is iteration-aware.
        var tableSchema = ScrubSchemaNameToken(template.TableSchema);
        var matViewSchema = ScrubSchemaNameToken(template.MaterializedViewSchema);
        var indexedViewSchema = ScrubSchemaNameToken(template.IndexedViewSchema);

        // Values are stored RAW (not single-quote-escaped). Escaping happens at substitution time,
        // context-aware, in SqlScript.TokenReplace — which doubles quotes only when the token lands
        // inside a SQL string literal. Pre-escaping here too double-escaped a cross-template token
        // embedded in a literal (e.g. '{{TableSchema_App}}' -> '''' ... ''''), producing invalid SQL
        // (#301). The current-template tokens (Template.cs) use a non-escaping substitution and so
        // pre-escape there; these go through TokenReplace, so they must not.
        var tokens = new Dictionary<string, string>
        {
            { $"TableSchema_{template.Name}", tableSchema },
            { $"ObjectScripts_{template.Name}", JsonHelper.Serialize(template.ObjectScripts.Concat(template.AfterTablesObjectScripts)) },
            { $"QueryTokens_{template.Name}", JsonHelper.Serialize(template.QueryTokens) },
            { $"MaterializedViewSchema_{template.Name}", matViewSchema },
            { $"IndexedViewSchema_{template.Name}", indexedViewSchema },
            // A2: cross-template XML twins. Stored RAW (not single-quote-escaped) exactly like their
            // JSON siblings above — escaping happens context-aware in SqlScript.TokenReplace, and
            // pre-escaping here would double-escape a token embedded in a literal (#301).
            { $"TableXml_{template.Name}", ModelXmlSerializer.ToIngestXml(EmptyArrayIfBlank(tableSchema), "Tables", "Table") },
            { $"MaterializedViewXml_{template.Name}", ModelXmlSerializer.ToIngestXml(EmptyArrayIfBlank(matViewSchema), "MaterializedViews", "MaterializedView") },
            { $"IndexedViewXml_{template.Name}", ModelXmlSerializer.ToIngestXml(EmptyArrayIfBlank(indexedViewSchema), "IndexedViews", "IndexedView") }
        };
        return tokens;
    }

    // ToIngestXml needs a JSON array; a template that declared no tables/views leaves the *Schema
    // snapshot blank, so normalize it to an empty array before conversion.
    private static string EmptyArrayIfBlank(string json) => string.IsNullOrWhiteSpace(json) ? "[]" : json;

    internal static bool ReferencesSpecialToken(SqlScript script) =>
        script.RemainingTokens.Any(token => SpecialTokenTags.Any(token.StartsWithIgnoringCase));

    private static string ScrubSchemaNameToken(string snapshot) =>
        string.IsNullOrEmpty(snapshot)
            ? snapshot
            : snapshot.Replace(Schema.Domain.SchemaDefaultResolver.SchemaNameToken, CrossTemplateSchemaPlaceholder);

    private List<Template> LoadTemplates()
    {
        var templates = new List<Template>();
        var specialTokens = new Dictionary<string, string>();
        foreach (var templateName in _product.TemplateOrder.Where(templateName => !string.IsNullOrWhiteSpace(templateName)))
        {
            _progressLog.Info($"Load Template Schema: {templateName}");
            var template = Template.Load(templateName, _product);
            foreach (var kvp in BuildSpecialTokens(template))
                specialTokens.Add(kvp.Key, kvp.Value);
            templates.Add(template);
        }

        _progressLog.Info("Check for Template Special Script Tokens");
        var scriptsWithSchemaTokens = templates.SelectMany(t => t.ScriptFolders).SelectMany(f => f.Scripts)
            .Where(ReferencesSpecialToken).ToList();
        if (scriptsWithSchemaTokens.Count > 0)
        {
            _progressLog.Info("Process Template Special Script Tokens");
            var tokenList = specialTokens.ToList();
            foreach (var script in scriptsWithSchemaTokens)
                script.ReplaceQueryTokens(tokenList);
        }

        return templates;
    }

    internal void QuenchProductScriptsWithCheckpoint(List<ProductFolder> folders, string msg, bool isBefore)
    {
        _updateFailed = false;

        if (_product.Platform == Platform.SqlServer && _secondaryServers.Count > 0)
        {
            // SQL Server: quench to primary and secondary servers in parallel, filtering by ServerToQuench
            var serverQueue = new TaskQueueManager<string>(_maxThreads);
            _secondaryServers.Union([_primaryServer]).Distinct().ToList()
                .ForEach(server => serverQueue.AddToQueue(server, s => QuenchScriptsToServerWithCheckpoint(s, msg,
                    folders.Where(f => s.Equals(_primaryServer) ? f.QuenchOnPrimary : f.QuenchOnSecondary).ToList(),
                    isBefore)));
            serverQueue.WaitForAll();
        }
        else
        {
            // PostgreSQL/MySQL: single server only
            QuenchScriptsToServerWithCheckpoint(_primaryServer, msg, folders, isBefore);
        }

        if (_updateFailed)
        {
            _anyFailure = true;
            EmitFailureRollup();
            throw new Exception("Product script quench FAILED");
        }
    }

    private void LogProductInfo()
    {
        _progressLog.Info($"ProductName: {_product.Name}, Platform: {_product.Platform}, TemplateOrder: [{string.Join(",", _product.TemplateOrder)}], ValidationScript: {_product.ValidationScript}");
        if (_product.ScriptTokens.Count == 0) return;

        LogScriptTokens("  Product Script Tokens:", _product.ScriptTokens);

        _progressLog.Info("");
    }

    /// <summary>
    /// Logs a token dictionary with sensitive values scrubbed (<see cref="LogScrubber"/>). When the
    /// operator sets <c>LogHygiene.LogTokens=false</c> the whole section is replaced by a single
    /// suppression notice — no token names and no values are emitted.
    /// </summary>
    private void LogScriptTokens(string header, IReadOnlyDictionary<string, string> tokens)
    {
        if (!_logHygiene.LogTokens)
        {
            _progressLog.Info($"{header} (suppressed via LogHygiene.LogTokens=false)");
            return;
        }

        _progressLog.Info(header);
        foreach (var token in tokens)
            _progressLog.Info($"    {token.Key}: {LogScrubber.ScrubTokenValue(token.Key, token.Value, _logHygiene)}");
    }

    private void TestServerConnections()
    {
        var shouldFail = false;
        var serverNames = new Dictionary<string, string>();
        _progressLog.Info("Testing connection to configured servers");
        var allServers = _secondaryServers.Union([_primaryServer]).ToList();

        foreach (var server in allServers)
        {
            if (serverNames.ContainsKey(server))
            {
                _progressLog.Error($"  Duplicate server configured: {server}");
                _errorLog.Error($"Duplicate server configured {server}");
                shouldFail = true;
                continue;
            }

            try
            {
                using var cmd = GetCommand(server);

                try
                {
                    cmd.CommandText = GetServerIdQuery(_product.Platform);
                    var serverName = cmd.ExecuteScalar()?.ToString() ?? "UNKNOWN";
                    serverNames.Add(server, serverName);

                    _progressLog.Info($"  {server} ({serverName}) connection succeeded");
                }
                finally
                {
                    cmd.Connection?.Close();
                    cmd.Connection?.Dispose();
                }
            }
            catch (Exception e)
            {
                _progressLog.Error($"  {server}: {e.Message} **CONNECTION FAILED**");
                _errorLog.Error($"Unable to connect to {server}:\r\n{e}");
                shouldFail = true;
            }
        }

        if (serverNames.Values.Distinct().Count() != serverNames.Count)
        {
            _progressLog.Error("  Duplicate server names detected while validating the configured server list");
            _errorLog.Error("Duplicate server names detected while validating the configured server list");
            shouldFail = true;
        }

        if (shouldFail) throw new Exception("Error validating configured servers");

        _progressLog.Info("");
        _progressLog.Info("");
    }

    private void QuenchScriptsToServerWithCheckpoint(string server, string message, IEnumerable<ProductFolder> folders, bool isBefore)
    {
        _progressLog.Info($"Quenching {message} Scripts to {server}");
        using var productScriptCommand = GetCommand(server);
        try
        {
            // Folder-level ShouldApplyExpression (#260): gate folders against this server before
            // flattening their scripts. Evaluated on the same server command the scripts run against.
            var scriptsToQuench = GateProductFolders(productScriptCommand, folders, server)
                .SelectMany(f => f.Scripts).Select(j => j.Clone()).ToList();
            QuenchScriptsWithCheckpoint(productScriptCommand, scriptsToQuench, server, isBefore);
        }
        catch
        {
            _updateFailed = true;
        }
        finally
        {
            productScriptCommand.Connection?.Close();
            productScriptCommand.Connection?.Dispose();
        }
    }

    private static void RemoveOldTableQuenchScripts()
    {
        var dir = DirectoryInfoFactory.GetFromFactory().GetDirectoryInfoWrapper(".");
        foreach (var file in dir.GetFiles("SchemaQuench - Quench Tables*.sql", SearchOption.TopDirectoryOnly))
            file.Delete();
    }

    private void QuenchTemplate(Template template, bool suppressKindling)
    {
        if (string.IsNullOrWhiteSpace(template.DatabaseIdentificationScript)) return;

        _progressLog.Info($"Quenching Template: {template.Name}");
        LogSchemaTemplateFieldsIfSet(template);
        if (template.LoggableTokens.Any())
            LogScriptTokens("Template Script Tokens:", template.LoggableTokens);

        _updateFailed = false;

        // Slice-3 fan-out: enumerate the flat work-unit list across all eligible servers, then
        // dispatch to a single MaxThreads-bounded pool. SQL Server's per-server ServerToQuench
        // selection is applied at enumeration; PostgreSQL/MySQL run against the primary server only.
        var workUnits = EnumerateWorkUnitsForTemplate(template);

        // Slice-5 selective execution (§9.3, §9.4): apply Target.Databases / Target.Schemas to
        // the per-template enumerated set, validating filter values against this template's
        // discovered universe. Target.Templates already filtered the template list upstream
        // before we got here, so we skip it on the per-template filter to avoid validating a
        // value already known to be in scope.
        //
        // Skip the filter entirely when this template already discovered zero work units —
        // e.g., a non-RequireAtLeastOneTarget template whose DatabaseIdentificationScript matched
        // nothing (the Initialize template in the TenantCRM demo after the database already
        // exists). Running an empty input through the filter would surface a misleading "filter
        // produced zero results" diagnostic, blaming the user's Target.* values for what is
        // actually an expected pass-through.
        try
        {
            workUnits = ApplyPerTemplateTargetFilter(template, workUnits);
        }
        catch (InvalidOperationException ex)
        {
            _progressLog.Error($"Target filter rejection for template '{template.Name}': {ex.Message}");
            _errorLog.Error($"Target filter rejection for template '{template.Name}': {ex.Message}");
            _updateFailed = true;
            _anyFailure = true;
            EmitFailureRollup();
            WriteDeploymentSummary(RunOutcome.Aborted, 2);
            LogBackup.BackupLogsAndExit("SchemaQuench", 2);
            return;
        }

        if (template.RequireAtLeastOneTarget && workUnits.Count == 0)
        {
            var targetKind = template.IsSchemaTemplate ? "(database, schema)" : "database";
            _progressLog.Error(
                $"No {targetKind} targets discovered for template '{template.Name}' " +
                $"(RequireAtLeastOneTarget: true)");
            _updateFailed = true;
        }

        if (workUnits.Count > 0)
        {
            DispatchWorkUnits(template, workUnits, suppressKindling);
        }

        if (!_updateFailed) return;

        _anyFailure = true;
        _progressLog.Error("One or more database quenches FAILED");

        // Per-template-scope failure routing: a template's TYPE determines which ContinueOn...
        // setting governs its failures. Schema templates respect ContinueOnSchemaFailure for
        // ANY failure inside their processing (discovery, reserved-name rejection, per-iteration
        // script failure, CREATE SCHEMA failure, deadlock surfaced via dispatcher exception).
        // Regular templates respect ContinueOnDatabaseFailure for ANY failure inside theirs.
        // This collapses the prior layered _dbFailure/_schemaFailure bits — one bad tenant
        // name no longer aborts an entire product run under ContinueOnDatabaseFailure: false
        // just because its rejection happened during discovery.
        if (!ShouldAbortOnFailure(template)) return;

        EmitFailureRollup();
        WriteDeploymentSummary(RunOutcome.Aborted, 2);
        LogBackup.BackupLogsAndExit("SchemaQuench", 2);
    }

    /// <summary>
    /// Returns whether a failure inside this template's processing should abort the product
    /// run. The relevant ContinueOn... setting is determined by template type: schema templates
    /// honor <see cref="Template.ContinueOnSchemaFailure"/>; regular templates honor
    /// <see cref="Template.ContinueOnDatabaseFailure"/>. The other setting is ignored for
    /// that template type — setting ContinueOnDatabaseFailure on a schema template (or vice
    /// versa) has no effect on the abort decision.
    /// </summary>
    private static bool ShouldAbortOnFailure(Template template) =>
        template.IsSchemaTemplate
            ? !template.ContinueOnSchemaFailure
            : !template.ContinueOnDatabaseFailure;

    /// <summary>
    /// Slice-5 selective execution: filters the loaded template list by <c>Target.Templates</c>
    /// (design §9.3). Returns <c>true</c> when the in-scope template list is non-empty and ready
    /// to iterate; <c>false</c> when the filter produced zero or rejected an unknown name —
    /// callers should return early. Logs the filter values and resolved unit-count summary so
    /// the user can verify their intent at a glance (§9.11).
    /// </summary>
    private bool TryFilterTemplatesByTarget(List<Template> templates, out List<Template> inScope)
    {
        inScope = templates;
        var anyFilter = _targetTemplates.Count > 0 || _targetDatabases.Count > 0 || _targetSchemas.Count > 0;
        if (anyFilter)
        {
            _progressLog.Info($"[Target] Templates: {WorkUnitFilter.FormatList(_targetTemplates)}");
            _progressLog.Info($"[Target] Databases: {WorkUnitFilter.FormatList(_targetDatabases)}");
            _progressLog.Info($"[Target] Schemas:   {WorkUnitFilter.FormatList(_targetSchemas)}");
        }

        if (_targetTemplates.Count == 0) return true;

        var loadedNames = templates.Select(t => t.Name).ToList();
        // Template-name matching is case-insensitive across the trio (validator,
        // TryFilterTemplatesByTarget, WorkUnitFilter). Template names are .NET-style identifiers
        // from Product.json and live independent of the engine's own database/schema casing
        // rules, so OrdinalIgnoreCase is the right choice here.
        var missing = _targetTemplates
            .Where(t => !loadedNames.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (missing.Count > 0)
        {
            var message =
                $"Target.Templates value(s) not present in the loaded template list: " +
                $"[{string.Join(",", missing)}]. Available: [{string.Join(",", loadedNames)}].";
            _progressLog.Error(message);
            _errorLog.Error(message);
            _anyFailure = true;
            EmitFailureRollup();
            WriteDeploymentSummary(RunOutcome.Aborted, 2);
            LogBackup.BackupLogsAndExit("SchemaQuench", 2);
            return false;
        }

        inScope = templates.Where(t => _targetTemplates.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        _progressLog.Info($"[Target] Resolved {inScope.Count} of {templates.Count} loaded template(s) in scope.");
        return true;
    }

    /// <summary>
    /// Per design §3.6: surfaces template-shape configuration through the startup log so a reader
    /// can confirm at-a-glance how the engine will treat this template. Schema templates echo their
    /// fan-out config (<c>SchemaIdentificationScript</c>, <c>CreateSchemaIfMissing</c>,
    /// <c>AllowParallel</c>, <c>ContinueOnSchemaFailure</c>) <b>unconditionally</b> — every schema
    /// template gets the four-line echo so ops can see the active settings without spelunking the
    /// template file. Regular templates skip those four lines entirely (no fan-out config to echo).
    /// <see cref="Template.ContinueOnDatabaseFailure"/> applies to all templates and is echoed only
    /// when set non-default (false); the default-true case stays silent for the 99% regular-template
    /// path. The deliberate verbosity for schema templates is the ops-friendly trade: echoing config
    /// that drives behavior is more informative than suppressing it.
    /// </summary>
    private void LogSchemaTemplateFieldsIfSet(Template template)
    {
        if (!template.IsSchemaTemplate && template.ContinueOnDatabaseFailure)
            return;

        if (template.IsSchemaTemplate)
        {
            _progressLog.Info($"  SchemaIdentificationScript: (set)");
            _progressLog.Info($"  CreateSchemaIfMissing: {template.CreateSchemaIfMissing}");
            _progressLog.Info($"  AllowParallel: {template.AllowParallel}");
            _progressLog.Info($"  ContinueOnSchemaFailure: {template.ContinueOnSchemaFailure}");
        }

        if (!template.ContinueOnDatabaseFailure)
            _progressLog.Info($"  ContinueOnDatabaseFailure: {template.ContinueOnDatabaseFailure}");
    }

    /// <summary>
    /// Builds the flat list of work units for this template across every eligible server.
    /// SQL Server respects <see cref="SqlServerTemplate.ServerToQuench"/> (Primary / Secondary / Both);
    /// PostgreSQL and MySQL run against the primary server only. For schema templates, each
    /// (server, database) pair invokes <see cref="SchemaDiscovery.Discover"/> on a live connection
    /// before producing one work unit per discovered schema. <para>
    /// Internal+virtual so tests can override and bypass live DB connections without re-implementing
    /// the SQL-Server-vs-other-platforms server-selection logic. The default implementation is what
    /// production code runs.</para>
    /// </summary>
    internal virtual List<WorkUnit> EnumerateWorkUnitsForTemplate(Template template, bool previewOnly = false)
    {
        var serverList = DetermineServerListForTemplate(template);
        var workUnits = new List<WorkUnit>();
        foreach (var server in serverList)
        {
            if (!EnumerateAndProvisionWorkUnitsForServer(template, server, workUnits, previewOnly))
            {
                if (ShouldAbortOnFailure(template))
                    break;
            }
        }
        return workUnits;
    }

    /// <summary>
    /// Returns the deduplicated server list a template runs against. SQL Server templates may
    /// override the default Primary-only behavior via <see cref="SqlServerTemplate.ServerToQuench"/>;
    /// PostgreSQL/MySQL templates always run against the primary server only.
    /// </summary>
    private List<string> DetermineServerListForTemplate(Template template)
    {
        if (_product.Platform != Platform.SqlServer)
            return new List<string> { _primaryServer };

        var serverToQuench = ServerToQuench.Primary;
        if (template is SqlServerTemplate sqlTemplate)
            serverToQuench = sqlTemplate.ServerToQuench;

        var list = new List<string>();
        if (serverToQuench is ServerToQuench.Primary or ServerToQuench.Both)
            list.Add(_primaryServer);
        if (serverToQuench is ServerToQuench.Secondary or ServerToQuench.Both)
            list.AddRange(_secondaryServers);
        return list.Distinct().ToList();
    }

    /// <summary>
    /// Runs <c>template.DatabaseIdentificationScript</c> on the given server, and for each returned
    /// database (a) opens a connection to enumerate schemas if the template is a schema template,
    /// (b) appends one or more work units to <paramref name="workUnits"/>. Failure routing:
    /// enumeration failure (server-level connection, identification script, schema-discovery script,
    /// server-level connection failures, bad <c>DatabaseIdentificationScript</c>, and per-DB
    /// schema-discovery failures are all classified as DB-level failures governed by
    /// <see cref="Template.ContinueOnDatabaseFailure"/>. When false, a failure sets
    /// <c>_updateFailed = true</c> and returns <c>false</c> — the caller breaks out of the
    /// server loop. When true, the failure is logged and <c>_updateFailed = true</c> is set,
    /// but enumeration continues to the next DB (for per-DB failures) or returns with
    /// whatever work units were already appended (for server-level failures). Returns
    /// <c>true</c> when enumeration completed for this server; <c>false</c> when a failure
    /// occurred and <c>ContinueOnDatabaseFailure</c> is false.</para>
    /// <para>
    /// DB-axis side-effect contract: when a <c>TemplateTargets</c> entry with
    /// <c>CreateIfMissing: true</c> references a missing database, this method invokes
    /// <see cref="SchemaProvisioner.EnsureDatabaseExists"/> against the admin connection
    /// (composed via <see cref="GetCommandForAdminDb"/>) BEFORE enqueueing any work units for
    /// that database. When <c>CreateIfMissing: false</c> and the database is missing, all
    /// work units targeting it are dropped from the queue and an info log line names the
    /// skipped database. The name reflects this dual responsibility (enumerate + provision).
    /// </para>
    /// </summary>
    private bool EnumerateAndProvisionWorkUnitsForServer(Template template, string server, List<WorkUnit> workUnits, bool previewOnly = false)
    {
        // TemplateTargets override: when an entry exists for this template, the matching
        // axis (Databases / Schemas) REPLACES the corresponding identification-script result. Each
        // axis decides independently — an entry may override one axis and leave the other to
        // discovery. The override is applied at this enumeration boundary so downstream dispatch /
        // checkpointing / per-iteration substitution treat override-sourced units identically to
        // discovery-sourced ones.
        _templateTargets.TryGetValue(template.Name, out var overrideEntry);
        var databasesOverride = overrideEntry is { Databases.Count: > 0 };
        var schemasOverride = overrideEntry is { Schemas.Count: > 0 };

        List<string> databases;
        string databaseSource;
        if (databasesOverride)
        {
            databases = new List<string>(overrideEntry!.Databases);
            databaseSource = $"TemplateTargets:{template.Name}:Databases";
            _progressLog.Info(
                $"[{server}] Template '{template.Name}' Databases sourced from " +
                $"TemplateTargets ({databases.Count} entr{(databases.Count == 1 ? "y" : "ies")}); " +
                $"DatabaseIdentificationScript bypassed.");
        }
        else
        {
            _progressLog.Info($"Locate Databases To Quench ({server})");
            try
            {
                using var command = GetCommandForIdentification(server, template);
                try
                {
                    command.CommandText = template.DatabaseIdentificationScript;
                    databases = new List<string>();
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        databases.Add($"{reader[0]}");
                }
                finally
                {
                    command.Connection?.Close();
                    command.Connection?.Dispose();
                }
            }
            catch (Exception e)
            {
                // Database enumeration failure (unreachable host, bad DatabaseIdentificationScript).
                // Failure scope follows the template's type: schema templates honor
                // ContinueOnSchemaFailure, regular templates honor ContinueOnDatabaseFailure.
                // When the relevant continue flag is true, log + trip _updateFailed + return true so
                // the caller continues to the next server (no work units were added for this server).
                // Schema templates carry an additional "[Schema: <enumeration>]" tag so a user
                // grepping the logs for "[Schema:" — the per-iteration scope marker — also catches
                // enumeration-phase failures that prevented any iteration from running. Regular
                // templates keep the bare "[server]" shape (no schema dimension).
                var schemaTemplateTag = template.IsSchemaTemplate ? " [Schema: <enumeration>]" : "";
                _progressLog.Error($"[{server}]{schemaTemplateTag} Database enumeration FAILED for template '{template.Name}': {e.Message}");
                _errorLog.Error($"[{server}]{schemaTemplateTag} Database enumeration failed (template '{template.Name}'):\r\n{e}");
                _updateFailed = true;
                return !ShouldAbortOnFailure(template);
            }
            databaseSource = "DatabaseIdentificationScript";
        }

        var schemaOverrideSource = $"TemplateTargets:{template.Name}:Schemas";

        foreach (var db in databases)
        {
            // DB-axis provisioning + skip-missing pre-dispatch pass: when the DB came from a
            // TemplateTargets:<T>:Databases override, check whether the database exists on the
            // server BEFORE running schema discovery (which assumes the DB exists) or enqueueing
            // any work units. CreateIfMissing: true → provision via admin connection (idempotent);
            // CreateIfMissing: false → emit skip log; DO NOT add work units for this (server, db).
            // Discovery-sourced DBs bypass this entirely.
            bool wouldCreateThisDb = false;
            if (databasesOverride)
            {
                var gate = DatabaseAxisProvisioningGate(server, db, template, overrideEntry!, previewOnly);
                if (!gate.Proceed) continue;
                wouldCreateThisDb = gate.WouldCreate;
            }

            if (template.IsSchemaTemplate)
            {
                List<string> schemas;
                string schemaSource;
                if (schemasOverride)
                {
                    schemas = new List<string>(overrideEntry!.Schemas);
                    schemaSource = schemaOverrideSource;
                }
                else
                {
                    try
                    {
                        schemas = DiscoverSchemas(server, db, template);
                    }
                    catch (Exception e)
                    {
                        // Per-DB schema-discovery failure inside a schema template (reserved-name
                        // guard, character-validation guard, bad SchemaIdentificationScript,
                        // connection failure to this DB). This is a SCHEMA-scope failure because
                        // it happened inside a schema template's processing — ContinueOnSchemaFailure
                        // governs whether to abort or continue to the next DB on this server.
                        // Tagged "[Schema: <enumeration>]" so the same grep that finds per-iteration
                        // log lines also catches enumeration failures that aborted before any
                        // tenant schema could be identified.
                        _progressLog.Error($"[{server}].[{db}] [Schema: <enumeration>] Schema discovery FAILED for template '{template.Name}': {e.Message}");
                        _errorLog.Error($"[{server}].[{db}] [Schema: <enumeration>] Schema discovery failed (template '{template.Name}'):\r\n{e}");
                        _updateFailed = true;
                        if (ShouldAbortOnFailure(template))
                            return false;
                        continue;
                    }
                    schemaSource = "SchemaIdentificationScript";
                }

                foreach (var schema in schemas)
                    workUnits.Add(new WorkUnit(server, db, template.Name, schema)
                    {
                        DatabaseSource = databaseSource,
                        SchemaSource = schemaSource,
                        // Carry the override origin + provisioning flag onto the unit so per-
                        // iteration deployment can branch on override-vs-discovery and on
                        // CreateIfMissing for the skip-missing path. ProvisionDatabaseIfMissing
                        // is informational at the unit level — the actual provisioning happened
                        // in DatabaseAxisProvisioningGate above; the flag surfaces in logs and
                        // tests to disambiguate override-driven units from discovery-sourced ones.
                        SchemaFromOverride = schemasOverride,
                        ProvisionSchemaIfMissing = schemasOverride && overrideEntry!.CreateIfMissing,
                        DatabaseFromOverride = databasesOverride,
                        ProvisionDatabaseIfMissing = databasesOverride && overrideEntry!.CreateIfMissing,
                        WouldCreateDatabase = wouldCreateThisDb
                    });
            }
            else
            {
                workUnits.Add(new WorkUnit(server, db, template.Name, "")
                {
                    DatabaseSource = databaseSource,
                    SchemaSource = "(regular template)",
                    DatabaseFromOverride = databasesOverride,
                    ProvisionDatabaseIfMissing = databasesOverride && overrideEntry!.CreateIfMissing,
                    WouldCreateDatabase = wouldCreateThisDb
                });
            }
        }

        return true;
    }

    /// <summary>
    /// Result of <see cref="DatabaseAxisProvisioningGate"/>: whether to proceed with
    /// enumerating work units for this (server, db) pair, and whether preview mode determined
    /// the database would be created on a real run.
    /// </summary>
    internal readonly record struct DbAxisGate(bool Proceed, bool WouldCreate);

    /// <summary>
    /// DB-axis provisioning gate. Called once per (server, db) pair when the DB came from a
    /// <c>TemplateTargets:&lt;T&gt;:Databases</c> override. Checks whether the database exists
    /// on <paramref name="server"/>; if missing, branches on <c>CreateIfMissing</c>:
    /// <list type="bullet">
    ///   <item><term>true</term><description>Provisions the DB via admin connection (idempotent)
    ///   unless <paramref name="previewOnly"/> is <c>true</c>, in which case logs "would create"
    ///   and proceeds without issuing any DDL.</description></item>
    ///   <item><term>false</term><description>Emits a skip log and returns <c>Proceed=false</c>
    ///   so the caller short-circuits without adding any work units for this DB.</description></item>
    /// </list>
    /// Returns <see cref="DbAxisGate.Proceed"/> = <c>true</c> when the caller should proceed
    /// with schema discovery / work-unit enqueueing; <c>false</c> when the DB is missing and
    /// skip-missing applies. <see cref="DbAxisGate.WouldCreate"/> is <c>true</c> only in
    /// preview mode when a missing-but-CreateIfMissing DB was identified without provisioning.
    /// <para>
    /// Internal+virtual so tests can override the entire decision flow without standing up a
    /// live admin connection. Production code uses the default implementation here, which
    /// composes the admin connection via <see cref="GetCommandForAdminDb"/> (which itself
    /// re-targets a <c>--ConnectionString</c> override through
    /// <see cref="ConnectionString.RetargetDatabase"/> when active) and delegates to
    /// <see cref="SchemaProvisioner.EnsureDatabaseExists"/>.
    /// </para>
    /// </summary>
    internal virtual DbAxisGate DatabaseAxisProvisioningGate(string server, string databaseName,
        Template template, TemplateTarget overrideEntry, bool previewOnly = false)
    {
        bool exists;
        try
        {
            exists = DatabaseExistsOnServer(server, databaseName);
        }
        catch (Exception e)
        {
            // Admin-connection failure here is a server-level failure (e.g., login denied to
            // master/postgres/information_schema). Surface a clear diagnostic and route through
            // the DB-failure path — the template's continue policy decides whether to abort.
            _progressLog.Error(
                $"[{server}] Database existence check FAILED for '{databaseName}' (template '{template.Name}'): {e.Message}");
            _errorLog.Error(
                $"[{server}] Database existence check failed for '{databaseName}' (template '{template.Name}'):\r\n{e}");
            _updateFailed = true;
            return new DbAxisGate(false, false);
        }

        if (exists) return new DbAxisGate(true, false);

        if (overrideEntry.CreateIfMissing)
        {
            if (previewOnly)
            {
                _progressLog.Info($"[{server}] Database '{databaseName}' does not exist — would be created (CreateIfMissing: true).");
                return new DbAxisGate(true, true);   // proceed for reporting; nothing provisioned
            }

            // Per-create log line surfaces from the provisioner (slice 3 / slice 4 contract:
            // ONE log line per create, branched by WhatIf inside the provisioner). The gate
            // owns ONLY the skip-missing and existence-check-failure logs — no pre-provision
            // chatter that would double-log every real CREATE DATABASE.
            try
            {
                ProvisionDatabaseViaAdminConnection(server, databaseName);
            }
            catch (Exception ex)
            {
                // Catch-all is intentional. The most likely failure is a
                // TemplateTargetProvisioningException wrapping a CREATE DATABASE permission denial,
                // but admin-connection blips (login denied to master, transient network failure)
                // surface directly from GetCommandForAdminDb without going through the provisioner's
                // wrapper. Both shapes are "treat as continue-on-failure" events — consistent with
                // how the per-DB iteration loop handles failures under ContinueOnDatabaseFailure.
                // The underlying engine exception is preserved as ex (and as InnerException when
                // the wrapper applies).
                _progressLog.Error(
                    $"[{server}] Database provisioning FAILED for '{databaseName}' (template '{template.Name}'): {ex.Message}");
                _errorLog.Error(
                    $"[{server}] Database provisioning failed for '{databaseName}' (template '{template.Name}'):\r\n{ex}");
                _updateFailed = true;
                return new DbAxisGate(false, false);
            }
            return new DbAxisGate(true, false);
        }

        // Skip-missing on the DB axis. Mirror the schema-axis log shape so a grep for
        // "CreateIfMissing is false" catches both axes. The "[server]" prefix already names
        // the server identity, so the message body says "this database" rather than repeating
        // the server name.
        _progressLog.Info(
            $"[{server}] Database '{databaseName}' does not exist and " +
            $"TemplateTargets CreateIfMissing is false — skipping all iterations for this server-database pair.");
        return new DbAxisGate(false, false);
    }

    /// <summary>
    /// Checks whether <paramref name="databaseName"/> exists on <paramref name="server"/> via an
    /// admin-database connection (per-engine: <c>master</c> / <c>postgres</c> /
    /// <c>information_schema</c>). Internal+virtual so tests can stub the existence answer
    /// without standing up a live connection. Production code routes through
    /// <see cref="GetCommandForAdminDb"/>, which uses
    /// <see cref="ConnectionString.RetargetDatabase"/> to compose the admin connection when a
    /// <c>--ConnectionString</c> override is active. PostgreSQL in particular
    /// REQUIRES the admin DB as the connection target — its <c>CREATE DATABASE</c> cannot run
    /// against a non-admin DB.
    /// </summary>
    internal virtual bool DatabaseExistsOnServer(string server, string databaseName)
    {
        using var command = GetCommandForAdminDb(server);  // targets the platform's init DB even with --ConnectionString override
        try
        {
            command.Parameters.Clear();
            command.CommandText = _product.Platform.GetBasePlatform() switch
            {
                Platform.SqlServer => "SELECT COUNT(*) FROM sys.databases WHERE name = @name",
                Platform.PostgreSQL => "SELECT COUNT(*) FROM pg_database WHERE datname = @name",
                Platform.MySQL => "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @name",
                _ => throw new ArgumentOutOfRangeException(nameof(databaseName))
            };
            var param = command.CreateParameter();
            param.ParameterName = "@name";
            param.Value = databaseName;
            param.DbType = DbType.String;
            command.Parameters.Add(param);
            var result = command.ExecuteScalar();
            command.Parameters.Clear();
            return ScalarToBool(result);
        }
        finally
        {
            command.Connection?.Close();
            command.Connection?.Dispose();
        }
    }

    /// <summary>
    /// Provisions <paramref name="databaseName"/> on <paramref name="server"/> via an
    /// admin-database connection. The admin DB is the engine's init DB (<c>master</c> /
    /// <c>postgres</c> / <c>information_schema</c>); <see cref="GetCommandForAdminDb"/> composes
    /// the connection via <see cref="ConnectionString.RetargetDatabase"/> when a
    /// <c>--ConnectionString</c> override is in effect so the admin DB is the connection target
    /// regardless of where the override pointed. The DDL itself is delegated to
    /// <see cref="SchemaProvisioner.EnsureDatabaseExists"/> so the per-engine quoting / escaping /
    /// IF-NOT-EXISTS gates stay testable in isolation. Internal+virtual so tests can override
    /// the provisioning step.
    /// </summary>
    internal virtual void ProvisionDatabaseViaAdminConnection(string server, string databaseName)
    {
        using var command = GetCommandForAdminDb(server);
        try
        {
            new SchemaProvisioner().EnsureDatabaseExists(command, databaseName, _product.Platform,
                IsWhatIfOnly, _progressLog.Info);
        }
        finally
        {
            command.Connection?.Close();
            command.Connection?.Dispose();
        }
    }

    /// <summary>
    /// Opens a per-DB connection and runs <see cref="SchemaDiscovery.Discover"/>. Factored out so
    /// tests can override schema discovery without standing up a live connection.
    /// </summary>
    internal virtual List<string> DiscoverSchemas(string server, string databaseName, Template template)
    {
        using var dbCommand = GetCommandForDatabase(server, databaseName);
        try
        {
            return SchemaDiscovery.Discover(dbCommand, template);
        }
        finally
        {
            dbCommand.Connection?.Close();
            dbCommand.Connection?.Dispose();
        }
    }

    /// <summary>
    /// Opens a command against a specific database on a server (vs. the platform-default init DB
    /// used by <see cref="GetCommand"/>). Used by schema discovery, which needs to query schemas
    /// in the actual target DB. Test override pattern same as <see cref="GetCommand"/>.
    /// </summary>
    internal virtual IDbCommand GetCommandForDatabase(string server, string databaseName)
    {
        var connectionStringOverride = CommandLineParser.ValueOfSwitch("ConnectionString", null);
        string connectionString;
        if (!string.IsNullOrEmpty(connectionStringOverride) && server == _primaryServer)
        {
            connectionString = ConnectionString.RetargetDatabase(connectionStringOverride, databaseName, _product.Platform);
        }
        else
        {
            connectionString = TargetConnectionString.Build(_product.Platform, server, databaseName, _config);
        }
        var factory = DbConnectionFactory.ForPlatform(_product.Platform);
        var connection = factory.GetDbConnection(connectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        return command;
    }

    /// <summary>
    /// Dispatches the enumerated work units through <see cref="WorkUnitDispatcher"/>. Each work
    /// unit's callback constructs a fresh <see cref="DatabaseQuench"/> and invokes
    /// <see cref="DatabaseQuench.Execute"/>; if <see cref="DatabaseQuench.QuenchSuccessful"/> is
    /// false, the callback throws to engage the dispatcher's failure path.
    /// <para>The dispatcher's <c>continueOnFailure</c> mode follows the template's scope-aware
    /// policy: a schema template honors <see cref="Template.ContinueOnSchemaFailure"/>, a
    /// regular template honors <see cref="Template.ContinueOnDatabaseFailure"/>. If the
    /// dispatcher surfaces an <see cref="AggregateException"/>, <c>_updateFailed</c> is set
    /// to true so the exit-code path engages regardless of which mode was active. Per-policy
    /// routing (abort vs continue at the product level) is decided downstream in
    /// <see cref="QuenchTemplate"/> via <see cref="ShouldAbortOnFailure"/>.</para>
    /// <para>Internal+virtual so tests can intercept the dispatch step.</para>
    /// </summary>
    internal virtual void DispatchWorkUnits(Template template, List<WorkUnit> workUnits, bool suppressKindling)
    {
        LogWorkUnitSources(workUnits);

        var allowParallel = new Dictionary<string, bool> { [template.Name] = template.AllowParallel };
        var dispatcher = new WorkUnitDispatcher(workUnits, _maxThreads, allowParallel,
            unit => RunOneWorkUnit(unit, template, suppressKindling),
            continueOnFailure: !ShouldAbortOnFailure(template));
        try
        {
            dispatcher.Run();
        }
        catch (AggregateException ae)
        {
            // AggregateException surfaces from both continue mode (all units attempted, some failed)
            // and abort mode (in-flight drained, remaining skipped). In both cases, trip _updateFailed
            // so QuenchTemplate can apply the correct exit / continue routing.
            _updateFailed = true;
            _progressLog.Error($"Template '{template.Name}' had {ae.InnerExceptions.Count} failed work unit(s)");
        }
    }

    /// <summary>
    /// Per-work-unit source disclosure: emit one log line per unit naming the
    /// source of both axes (script vs TemplateTargets override). The format is greppable both
    /// per <c>[server].[db]</c> and per <c>source:</c> tag so a reader can scan a large log for
    /// "which units came from a config override?" in one pass. Factored out of
    /// <see cref="DispatchWorkUnits"/> so the log format is testable without spinning up the
    /// dispatcher itself.
    /// </summary>
    internal void LogWorkUnitSources(IReadOnlyList<WorkUnit> workUnits)
    {
        foreach (var unit in workUnits)
            _progressLog.Info(
                $"[{unit.Server}].[{unit.DatabaseName}]" +
                $"{(string.IsNullOrEmpty(unit.SchemaName) ? "" : $" [Schema: {unit.SchemaName}]")} " +
                $"Dispatching work unit (source: db={unit.DatabaseSource}, schema={unit.SchemaSource})");
    }

    /// <summary>
    /// The dispatcher callback: build a <see cref="DatabaseQuench"/> for the work unit and run it.
    /// Throws on failure so the dispatcher's failure path (continue or abort, per
    /// <see cref="Template.ContinueOnSchemaFailure"/>) is engaged.
    /// </summary>
    private void RunOneWorkUnit(WorkUnit unit, Template template, bool suppressKindling)
    {
        var dropRemovedTables = FormatBooleanFlag(ResolveCascadedFlag(
            ConfigBool(_config, SettingsKeys.DropTablesRemovedFromProduct), _product.DropTablesRemovedFromProduct,
            template.DropTablesRemovedFromProduct, defaultValue: true));
        var dropRemovedColumns = FormatBooleanFlag(ResolveCascadedFlag(
            ConfigBool(_config, SettingsKeys.DropColumnsRemovedFromProduct), _product.DropColumnsRemovedFromProduct,
            template.DropColumnsRemovedFromProduct, defaultValue: true));
        var dropRemovedForeignKeys = FormatBooleanFlag(ResolveCascadedFlag(
            ConfigBool(_config, SettingsKeys.DropForeignKeysRemovedFromProduct), _product.DropForeignKeysRemovedFromProduct,
            template.DropForeignKeysRemovedFromProduct, defaultValue: true));
        var dropRemovedCheckConstraints = FormatBooleanFlag(ResolveCascadedFlag(
            ConfigBool(_config, SettingsKeys.DropCheckConstraintsRemovedFromProduct), _product.DropCheckConstraintsRemovedFromProduct,
            template.DropCheckConstraintsRemovedFromProduct, defaultValue: true));
        var dropRemovedExcludeConstraints = FormatBooleanFlag(ResolveCascadedFlag(
            ConfigBool(_config, SettingsKeys.DropExcludeConstraintsRemovedFromProduct), _product.DropExcludeConstraintsRemovedFromProduct,
            template.DropExcludeConstraintsRemovedFromProduct, defaultValue: true));
        var dropRemovedStatistics = FormatBooleanFlag(ResolveCascadedFlag(
            ConfigBool(_config, SettingsKeys.DropStatisticsRemovedFromProduct), _product.DropStatisticsRemovedFromProduct,
            template.DropStatisticsRemovedFromProduct, defaultValue: true));
        var dropRemovedIndexes = FormatBooleanFlag(ResolveCascadedFlag(
            ConfigBool(_config, SettingsKeys.DropIndexesRemovedFromProduct), _product.DropIndexesRemovedFromProduct,
            template.DropIndexesRemovedFromProduct, defaultValue: true));
        // Environment tier only. The table tier lives on MariaDbTable and is resolved inside the proc,
        // because putting it on the shared Table type would publish a MariaDB-only setting into the
        // SQL Server, PostgreSQL and MySQL schemas -- the exact leak MariaDbTable exists to prevent.
        // Default FALSE, unlike every sibling above: a package that cannot declare its periods must not
        // have them dropped for not declaring them.
        var dropRemovedPeriods = ConfigBool(_config, SettingsKeys.DropPeriodsRemovedFromProduct) ?? false;
        var dropUnknownIndexes = ResolveCascadedFlag(
            ConfigBool(_config, SettingsKeys.DropUnknownIndexes), _product.DropUnknownIndexes,
            template.DropUnknownIndexes, defaultValue: false);
        // No-drop protection tier (#270 Slice E): a protected environment never drops an object BY
        // ABSENCE. Force every Drop…RemovedFromProduct lever (and DropUnknownIndexes) false so the
        // by-absence passes are suppressed; the procs still record what they skipped for the manifest.
        // Transient/for-change drop-then-recreate is gated by change detection, not these flags, so it
        // is unaffected (verified across all three engines).
        if (_protectedMode)
        {
            dropRemovedTables = FormatBooleanFlag(false);
            dropRemovedColumns = FormatBooleanFlag(false);
            dropRemovedForeignKeys = FormatBooleanFlag(false);
            dropRemovedCheckConstraints = FormatBooleanFlag(false);
            dropRemovedExcludeConstraints = FormatBooleanFlag(false);
            dropRemovedStatistics = FormatBooleanFlag(false);
            dropRemovedIndexes = FormatBooleanFlag(false);
            dropUnknownIndexes = false;
            dropRemovedPeriods = false;
        }
        // The three UPPER tiers, collapsed here to one whole policy. The table tier is deliberately not
        // resolved here: it lives in the parsed working set inside the database, alongside the sentinel
        // that says whether the table declared a policy at all, so the proc picks between this policy and
        // the table's own — whole, never field by field.
        var rebuildPolicy = ResolveCascadedPolicy(
            ReadEnvRebuildPolicy(_config), _product.RebuildPolicy, template.RebuildPolicy, table: null);
        var quench = new DatabaseQuench(unit.Server, _product, template, unit.DatabaseName, unit.SchemaName,
            suppressKindling, _whatIfOnly, _runScriptsTwice, dropRemovedTables,
            dropRemovedColumns, dropRemovedForeignKeys, dropRemovedCheckConstraints, dropRemovedExcludeConstraints, dropRemovedStatistics, dropRemovedIndexes, dropUnknownIndexes,
            _updateTables && template.Tables.Count > 0, _deliverData, _checkpointing,
            _trackRunOnceMigrations, _pruneObsoleteMigrationTracking, _forceReKindle)
        {
            // Carry the override-origin signals through so EnsureSchemaExists can branch on
            // TemplateTargets-driven provisioning + skip-missing. Defaults preserve existing
            // behavior for discovery-sourced units.
            SchemaFromOverride = unit.SchemaFromOverride,
            ProvisionSchemaIfMissing = unit.ProvisionSchemaIfMissing,
            RunTiming = _runTiming,
            MigrationScripts = _migrationScripts,
            WhatIf = _whatIf,
            ChangeAudit = _changeAudit,
            CaptureWouldDrop = _protectedMode,
            CascadedRebuildPolicy = rebuildPolicy,
            DropPeriodsRemovedFromProduct = dropRemovedPeriods,
            DropSchemaBoundDependents = ResolveCascadedFlag(
                ConfigBool(_config, SettingsKeys.DropSchemaBoundDependents), _product.DropSchemaBoundDependents,
                template.DropSchemaBoundDependents, defaultValue: false),
            // Environment-level only for now: an event is a template-scoped object, so there is no
            // per-table or per-object level for this to cascade from. Forced OFF under the no-drop
            // protection tier (PreventDrop) exactly like every other drop-by-absence pass -- the tier's
            // contract is that nothing is dropped for being absent from the product, events included.
            DropRemovedEvents = !_protectedMode && (ConfigBool(_config, SettingsKeys.DropEventsRemovedFromProduct) ?? false)
        };
        var workUnitStopwatch = Stopwatch.StartNew();
        quench.Execute();
        workUnitStopwatch.Stop();
        _targetResults.Add(new TargetResult(
            quench.LogPrefix, unit.Server, unit.DatabaseName, unit.SchemaName ?? "", unit.TemplateName,
            TargetResult.DeriveOutcome(quench.QuenchSuccessful, quench.WasSkipped), workUnitStopwatch.ElapsedMilliseconds));
        if (!quench.QuenchSuccessful)
        {
            if (quench.LastFailure != null) _failureRecords.Add(quench.LastFailure);
            // Throw so the dispatcher records this failure. In continue mode the dispatcher
            // proceeds to the next unit; in abort mode it drains in-flight units and stops.
            var schemaSuffix = string.IsNullOrEmpty(unit.SchemaName) ? "" : $" [Schema: {unit.SchemaName}]";
            throw new Exception($"Work unit failed: {unit.Server}.{unit.DatabaseName}{schemaSuffix} (template {unit.TemplateName})");
        }
    }

    private void QuenchScriptsWithCheckpoint(IDbCommand destCmd, List<SqlScript> scriptList, string server, bool isBefore)
    {
        var initDb = GetInitDatabase(_product.Platform);
        var serverMsg = string.IsNullOrWhiteSpace(server) ? "" : $"[{server}].";
        var slot = isBefore ? "Before" : "After";
        var failingBatchIndexes = new Dictionary<SqlScript, int>();

        foreach (var script in scriptList)
        {
            if (_checkpointing.HasCompletedScript(ProductScopeForServer(server), slot, script.LogPath))
            {
                _progressLog.Info($"{serverMsg}[{initDb}]    {(IsWhatIfOnly ? "Would SKIP" : "Skipping")} (previously quenched per checkpoint) {script.LogPath}");
                script.HasBeenQuenched = true;
                continue;
            }

            if (IsWhatIfOnly)
            {
                _progressLog.Info($"{serverMsg}[{initDb}]    Would Quench {script.LogPath}");
                script.HasBeenQuenched = true;
                continue;
            }

            _progressLog.Info($"{serverMsg}[{initDb}]    Quenching {script.LogPath}");
            var needDBReset = false;
            var currentBatchIndex = -1;
            try
            {
                script.CheckForUnresolvedTokens(initDb, serverMsg, _progressLog.Warn);
                for (var i = 0; i < (_runScriptsTwice ? 2 : 1); i++)
                {
                    for (var batchIndex = 0; batchIndex < script.Batches.Count; batchIndex++)
                    {
                        currentBatchIndex = batchIndex;
                        var batch = script.Batches[batchIndex];
                        needDBReset = needDBReset || batch.ContainsIgnoringCase("USE ");
                        destCmd.CommandText = batch;
                        destCmd.ExecuteNonQuery();
                    }
                }

                script.HasBeenQuenched = true;
                script.Error = null;
                _checkpointing.MarkScriptCompleted(ProductScopeForServer(server), slot, script.LogPath);
                _migrationScripts.Record(server, "", "", "", slot, MakeScriptPathRelative(script.LogPath, Path.GetDirectoryName(_product.FilePath) ?? ""));
            }
            catch (Exception ex)
            {
                script.Error = ex;
                // currentBatchIndex is the batch that was executing when the exception was thrown;
                // -1 (never entered the batch loop, e.g. token-check threw first) falls back to the last batch.
                failingBatchIndexes[script] = currentBatchIndex >= 0 ? currentBatchIndex : script.Batches.Count - 1;
            }
            finally
            {
                if (needDBReset) ResetDb(destCmd);
            }
        }

        if (scriptList.Any(x => !x.HasBeenQuenched))
        {
            var directory = ResolveArtifactDirectory();
            var scrub = ScrubArtifactsEnabled;
            var sensitiveValues = SensitiveTokenValues();

            foreach (var sqlScript in scriptList.Where(s => !s.HasBeenQuenched))
            {
                // A mid-script server drop (restart/crash/OOM) lands in sqlScript.Error as a raw
                // provider disconnect. Lead with the purpose-built message naming THIS server (the
                // per-server product-script loop covers secondaries too); keep the full stack in the
                // error log. Otherwise surface the raw error as before.
                var connectionLost = ConnectionLostClassifier.IsConnectionLost(sqlScript.Error);
                var scriptError = connectionLost
                    ? ConnectionLostMessage.Build(server, isBefore ? "BeforeScripts" : "AfterScripts")
                    : sqlScript.Error?.Message ?? $"Unable to quench '{sqlScript.LogPath}'";

                if (connectionLost)
                {
                    _progressLog.Error($"{serverMsg}[{initDb}] {scriptError}");
                    _errorLog.Error($"{serverMsg}[{initDb}] Lost connection quenching '{sqlScript.LogPath}'", sqlScript.Error);
                }
                else
                    _progressLog.Error($"{serverMsg}[{initDb}] Unable to quench '{sqlScript.LogPath}':\r\n{sqlScript.Error}");

                string artifactPath = null;
                try
                {
                    var header = $"Failed: {serverMsg}[{initDb}] [{sqlScript.LogPath}] — {sqlScript.Error?.Message}";
                    var fileName = GetProductDebugFileName($"Failed {Path.GetFileNameWithoutExtension(sqlScript.Name)}", server);
                    var failingBatchIndex = failingBatchIndexes.GetValueOrDefault(sqlScript, sqlScript.Batches.Count - 1);
                    artifactPath = ResolvedSqlArtifactWriter.WriteFailureArtifact(directory, scrub, sensitiveValues,
                        header, sqlScript.Batches, failingBatchIndex, fileName);
                    _progressLog.Error($"{serverMsg}[{initDb}]    Resolved SQL written to: {artifactPath}");
                }
                catch (Exception artifactEx)
                {
                    // Degrade gracefully: the original script error is already logged above; losing the
                    // artifact must not mask it.
                    _progressLog.Error($"{serverMsg}[{initDb}]    Could not write resolved-SQL artifact for '{sqlScript.LogPath}': {artifactEx.Message}");
                }

                // Server-scope failure capture (Before/After product scripts) → end-of-run roll-up.
                _failureRecords.Add(new FailureRecord(
                    isBefore ? "BeforeScripts" : "AfterScripts", $"[{server}]",
                    scriptError,
                    new[] { $"Unable to quench '{sqlScript.LogPath}'" }, artifactPath));
            }

            throw new Exception($"{serverMsg}[{initDb}] Unable to quench one or more scripts");
        }
    }

    /// <summary>
    /// Config resolution for the product-level resolved-SQL artifact surface (#327 S4.3), mirroring
    /// <c>DatabaseQuench.ResolveArtifactDirectory</c> / <c>ScrubArtifactsEnabled</c> / <c>SensitiveTokenValues</c>.
    /// ProductQuench cannot call those (internal to DatabaseQuench, a different class) so it resolves
    /// config itself. Product-scoped only: template tokens don't apply to product-level Before/After scripts.
    /// </summary>
    private string ResolveArtifactDirectory()
    {
        var configured = _config[SettingsKeys.ArtifactPath];
        return string.IsNullOrWhiteSpace(configured) ? Directory.GetCurrentDirectory() : configured;
    }

    private bool ScrubArtifactsEnabled => _config[SettingsKeys.ScrubArtifacts]?.ToLower() == "true";

    private IReadOnlyList<KeyValuePair<string, string>> SensitiveTokenValues() =>
        _product.ScriptTokens
            .Where(kv => LogScrubber.ShouldScrubName(kv.Key, _logHygiene))
            .ToList();

    /// <summary>
    /// Debug filename for a product-level script failure artifact. There is no database/schema scope
    /// at this level (product Before/After scripts run before/around per-database template work), so
    /// the product name + server stand in for the scope DatabaseQuench.GetDebugFileName uses.
    /// </summary>
    private string GetProductDebugFileName(string label, string server)
    {
        var serverSuffix = string.IsNullOrEmpty(server) ? "" : $" {server}";
        return $"SchemaQuench - {label}{serverSuffix} {_product.Name}.sql";
    }

    private void ResetDb(IDbCommand destCmd)
    {
        try
        {
            var initDb = GetInitDatabase(_product.Platform);
            destCmd.CommandText = _product.Platform.GetBasePlatform() switch
            {
                Platform.SqlServer => $"USE [{initDb}]",
                Platform.PostgreSQL => initDb, // PostgreSQL uses ChangeDatabase
                Platform.MySQL => $"USE `{initDb}`",
                _ => throw new ArgumentOutOfRangeException()
            };

            if (_product.Platform == Platform.PostgreSQL)
                destCmd.Connection.ChangeDatabase(initDb);
            else
                destCmd.ExecuteNonQuery();
        }
        catch
        {
            // ignore error resetting db
        }
    }
}
