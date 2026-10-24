// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace Schema.Domain;

/// <summary>
/// Records a table / materialized-view / indexed-view component file that
/// <see cref="Template.Load"/> could not even parse as JSON when its <c>tolerateComponentLoadErrors</c>
/// flag is set (--Validate's lenient load). Reserved for genuine syntax failures — a file that
/// parses but fails member/shape validation (e.g. a misnamed property) is deliberately NOT recorded
/// here, because <c>JsonSchemaCheck</c>'s independent on-disk pass already reports that case
/// precisely as SS-JSON-001; recording it here too would collapse two different problems onto one
/// code. See <see cref="Template"/>'s component loaders for the exception-type check that decides
/// which bucket a given failure falls into.
/// </summary>
/// <param name="IsValidJson">
/// True when the file parsed as JSON but would not load (an unrecognised property, say); false when it
/// is not valid JSON at all. Consumers listing "files that failed to load" want every entry; `--Validate`
/// reports only the unparseable ones as SS-LOAD-001, because JsonSchemaCheck's independent pass already
/// describes a parseable-but-wrong file more precisely as SS-JSON-001.
/// </param>
/// <param name="TableName">
/// The Name a table file declares, when it parsed far enough to read one: the table is declared, so a foreign key into
/// it is not dangling, only unloaded.
/// </param>
public sealed record ComponentLoadError(string FilePath, string Message, bool IsValidJson, string TableName = null);
