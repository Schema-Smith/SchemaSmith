// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Schema.Validation.Checks;

namespace Schema.Validation;

/// <summary>
/// Source of the checks run by `--Validate`. Slice 1 shipped this empty (the gate only exercised
/// the package-load path, SS-LOAD-001). Real <see cref="ISchemaCheck"/> implementations are
/// appended here as later slices land.
/// </summary>
public static class ValidationCheckRegistry
{
    public static IReadOnlyList<ISchemaCheck> Default() => new List<ISchemaCheck>
    {
        new DuplicationCheck(),
        new CoherenceCheck(),
        new TokenCheck(),
        new TableFileNameCheck(),
        new DeprecationCheck(),
        new JsonSchemaCheck()
    };
}
