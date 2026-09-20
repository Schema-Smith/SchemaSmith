// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace SchemaQuench;

/// <summary>
/// An atomic unit of work dispatched by <see cref="WorkUnitDispatcher"/>: one
/// <c>(server, database, template, schema)</c> tuple. The pipeline executes each
/// work unit independently, with at most <c>MaxThreads</c> running concurrently
/// (subject to per-template serial-queue carveouts via <c>Template.AllowParallel</c>).
///
/// <para>
/// For regular (non-schema) templates, <see cref="SchemaName"/> is the empty string —
/// the template runs once per database with <c>{{SchemaName}}</c> undefined. For
/// schema templates, <see cref="SchemaName"/> is one of the names returned by the
/// template's <c>SchemaIdentificationScript</c> (design §5.1).
/// </para>
/// </summary>
/// <param name="Server">Target database server identifier (host name, ServerToQuench resolution).</param>
/// <param name="DatabaseName">Target database name as returned by <c>DatabaseIdentificationScript</c>.</param>
/// <param name="TemplateName">The template this unit executes — also the key for the AllowParallel map.</param>
/// <param name="SchemaName">
/// The iteration schema for schema templates, or empty string for regular templates.
/// Empty-string sentinel is intentional: it matches the persisted migration-tracking
/// schema-name column convention (slice 2) where regular-template rows store '' rather than NULL.
/// </param>
public sealed record WorkUnit(
    string Server,
    string DatabaseName,
    string TemplateName,
    string SchemaName)
{
    /// <summary>
    /// Discloses how <see cref="DatabaseName"/> was obtained — either the user-facing
    /// <c>DatabaseIdentificationScript</c> result or a config-driven
    /// <c>TemplateTargets:&lt;TemplateName&gt;:Databases</c> override. Carried into the deployment
    /// summary report (<c>TargetSummary.DatabaseSource</c>), which keeps one row per work unit; the
    /// progress log shows only a per-template rollup of these. Default empty so existing call sites
    /// and tests that don't care about the disclosure stay unaffected.
    /// </summary>
    public string DatabaseSource { get; init; } = "";

    /// <summary>
    /// Discloses how <see cref="SchemaName"/> was obtained — either the
    /// <c>SchemaIdentificationScript</c> result, a <c>TemplateTargets:&lt;Name&gt;:Schemas</c>
    /// override, or the regular-template sentinel for non-schema templates.
    /// Default empty so existing call sites and tests that don't care about the disclosure
    /// stay unaffected.
    /// </summary>
    public string SchemaSource { get; init; } = "";

    /// <summary>
    /// True when this unit's <see cref="SchemaName"/> came from a
    /// <c>TemplateTargets:&lt;Name&gt;:Schemas</c> override. Used by per-iteration
    /// deployment to distinguish override-sourced units (which honor
    /// <see cref="ProvisionSchemaIfMissing"/> for the skip-missing path) from
    /// discovery-sourced units (which keep today's strict <c>CreateSchemaIfMissing</c>
    /// behavior). Default false so non-override units keep current behavior unchanged.
    /// </summary>
    public bool SchemaFromOverride { get; init; }

    /// <summary>
    /// True when an override declared <c>CreateIfMissing: true</c> for this unit's template.
    /// Per-iteration deployment provisions a missing schema via
    /// <see cref="SchemaProvisioner.EnsureSchemaExists(System.Data.IDbCommand, string, Schema.Domain.Platform, bool, System.Action{string})"/>
    /// before running deployment work. When false (the default), missing schemas on
    /// override-sourced units are SKIPPED with an info log; missing schemas on
    /// discovery-sourced units continue to honor the template-level
    /// <c>CreateSchemaIfMissing</c> contract.
    /// </summary>
    public bool ProvisionSchemaIfMissing { get; init; }

    /// <summary>
    /// True when this unit's <see cref="DatabaseName"/> came from a
    /// <c>TemplateTargets:&lt;Name&gt;:Databases</c> override. Drives the
    /// pre-dispatch database-existence check + provisioning / skip-missing decision in
    /// <see cref="ProductQuench.EnumerateAndProvisionWorkUnitsForServer"/>. Discovery-sourced units
    /// (default false) bypass the DB-axis pre-dispatch pass entirely — today's behavior
    /// is preserved bit-for-bit.
    /// </summary>
    public bool DatabaseFromOverride { get; init; }

    /// <summary>
    /// True when an override declared <c>CreateIfMissing: true</c> on a template carrying a
    /// <c>Databases</c> override. When set, the pre-dispatch DB-existence pass
    /// provisions a missing database via
    /// <see cref="SchemaProvisioner.EnsureDatabaseExists(System.Data.IDbCommand, string, Schema.Domain.Platform, bool, System.Action{string})"/>
    /// before the per-database iteration's connection opens. When false (the default), missing
    /// databases on override-sourced units are SKIPPED with an info log — the work unit is
    /// removed from the dispatch queue.
    /// </summary>
    public bool ProvisionDatabaseIfMissing { get; init; }

    /// <summary>
    /// True when the DB-axis provisioning gate ran in preview-only mode and determined that
    /// this unit's database does not exist but <c>CreateIfMissing: true</c> would cause it to
    /// be created on a real run. No <c>CREATE DATABASE</c> was issued; the unit is still
    /// enumerated so the pre-flight preview report can surface it as "would be created".
    /// Default false so non-preview units and existing-DB units stay unaffected.
    /// </summary>
    public bool WouldCreateDatabase { get; init; }
}
