// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace SchemaTongs;

/// <summary>
/// The statement form a SQL Server module script is written in. <see cref="CreateOrAlter"/> needs 2016 SP1;
/// <see cref="Legacy"/> is a guarded CREATE stub followed by ALTER, which every supported version accepts.
/// </summary>
public enum ModuleSyntax
{
    CreateOrAlter,
    Legacy
}
