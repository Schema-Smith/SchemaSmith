// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace Schema.Domain;

/// <summary>
/// A deprecated alias that load migrated so the package keeps working. Recorded on the template because a
/// progress-log warning alone is invisible to <c>--Validate</c>, which reported such a package as a clean
/// PASS; <c>DeprecationCheck</c> turns each notice into a Warning finding.
/// </summary>
/// <param name="Code">The finding code, one per alias (<c>SS-DEP-*</c>).</param>
/// <param name="Location">The file, or the template/table position when no file is known.</param>
/// <param name="Message">What is deprecated and what to write instead.</param>
public sealed record DeprecationNotice(string Code, string Location, string Message);
