// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace Schema.Domain;

/// <summary>
/// A declaration load changed so that it deploys: a nullable primary-key column made NOT NULL (SS-COL-008), a
/// PostgreSQL name unwrapped from double quotes (SS-IDENT-003). Recorded so the deploy warns before it applies the
/// change and <c>--Validate</c> reports the declaration as a warning under <paramref name="Code"/>.
/// </summary>
public sealed record LoadNotice(string Code, string Location, string Message);
