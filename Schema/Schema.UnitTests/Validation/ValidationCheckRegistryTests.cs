// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Validation;

namespace Schema.UnitTests.Validation;

[TestFixture]
public class ValidationCheckRegistryTests
{
    private static IEnumerable<TestCaseData> Checks() =>
        ValidationCheckRegistry.Default().Select(c => new TestCaseData(c).SetArgDisplayNames(c.GetType().Name));

    // --Validate stops at SS-LOAD-003 before a check sees a product with no platform, but a check is public API and a
    // direct caller gets no such guard: resolving the base platform of Unknown throws.
    [TestCaseSource(nameof(Checks))]
    public void EveryCheck_RunsOnAProductWithNoPlatform(ISchemaCheck check)
    {
        var template = new Template { Name = "T" };
        template.Tables.Add(new SqlServerTable { Name = "Customer", Schema = "dbo", Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } } });
        var context = new ValidationContext(new Product { Name = "Acme", Platform = Platform.Unknown, TemplateOrder = [] }, [template], "pkg");

        Assert.DoesNotThrow(() => check.Run(context).ToList());
    }
}
