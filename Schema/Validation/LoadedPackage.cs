// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Schema.Domain;

namespace Schema.Validation;

/// <summary>
/// A fully loaded schema package: the <see cref="Product"/> plus every <see cref="Template"/>
/// named in its TemplateOrder. Templates are not attached to Product (mirrors
/// <c>ProductQuench.LoadTemplates</c>) — this record carries them alongside it so
/// <see cref="ValidationContext"/> and in-memory test fixtures don't need a live Product/disk
/// round-trip to assemble a package.
/// <para>
/// <paramref name="LoadFindings"/> carries findings produced DURING loading itself — SS-LOAD-001
/// for a component file PackageLoader's lenient load skipped because it wasn't valid JSON at all
/// (see <see cref="Template.ComponentLoadErrors"/>), SS-LOAD-002 for a whole template excluded
/// because its own <c>Template.json</c> couldn't be loaded at all (see
/// <c>Schema.Validation.PackageLoader.TryLoadTemplate</c>), and SS-TOK-004 for a product- or
/// template-level <c>&lt;*File*&gt;</c> ScriptTokens entry that couldn't be resolved (see
/// <see cref="Product.FileTokenErrors"/> / <see cref="Template.FileTokenErrors"/>). Defaults to
/// null/empty so existing in-memory test fixtures that construct a <see cref="LoadedPackage"/>
/// directly (no disk load, nothing to skip) don't need updating.
/// </para>
/// <para>
/// <paramref name="UnlessNamedLoadFindings"/> carries SS-LOAD-001 for a component file that IS valid JSON
/// but still failed to load. A schema violation in it is reported precisely by <c>JsonSchemaCheck</c>, so
/// the validator prints one of these only when no check's finding names the same file; a rule only the
/// loader enforces (a converter's) would otherwise leave the file dropped with nothing said.
/// </para>
/// </summary>
public sealed record LoadedPackage(Product Product, IReadOnlyList<Template> Templates, IReadOnlyList<Finding> LoadFindings = null,
    IReadOnlyList<Finding> UnlessNamedLoadFindings = null);
