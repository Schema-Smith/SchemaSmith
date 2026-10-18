# Why SchemaSmith

What if deploying a database change felt as routine as committing code?

You know the reality today. It's Thursday afternoon and someone needs a column added to a production table. So you write an ALTER script, test it against a dev copy that's three weeks stale, cross your fingers, and run it in production. It works -- this time. Next month, a different script fails because someone else already renamed that index and nobody updated the migration folder. Now you're debugging deployment archaeology at 10pm.

Hand-written migration scripts are the status quo, and the status quo is fragile. Every ALTER is a bet that you know exactly what the target database looks like right now. Migration folders grow into long, ordered chains where one bad link breaks everything downstream. DBAs spend their review cycles reading procedural diffs -- "add this column, drop that index, rename this constraint" -- instead of reviewing the actual table design. And when something drifts, the answer to "who changed this column and when?" lives in a ticket somewhere, maybe.

Deployment fear slows the whole team down. Developers wait for DBA approval. DBAs wait for confidence that the script matches reality. Everyone waits because the cost of getting it wrong is a production outage.

There's a better model, and you already use it for everything else.

## Declare the state, not the steps

You don't write diffs of your C# classes and apply them one by one. You declare the class and the compiler figures out the rest. Your infrastructure team doesn't hand-write sequential cloud change scripts -- they declare the desired state in Terraform and let the tool compute the delta.

SchemaSmith brings that same model to relational databases. You declare what every table, view, procedure, and trigger should look like. The tool compares your declaration against the live database, computes what changed, and generates the correct DDL. You review structure, not migration steps. The database converges to match your declaration every time, on every target.

No migration scripts. No dependency ordering. No guessing what the target looks like. You describe the destination, and the forge does the rest.

## One toolset, four engines

SchemaSmith supports **SQL Server**, **PostgreSQL**, **MySQL**, and **MariaDB** as first-class peers. Not "SQL Server with PostgreSQL bolted on." Four adapters, one shared package *format*, one workflow, one mental model. Each package declares its own platform and speaks that engine's native DDL -- and packages are platform-specific, not interchangeable: a MariaDB package targets MariaDB, a MySQL package targets MySQL, and neither applies to the other. Whether your team runs a single platform or a heterogeneous mix, SchemaSmith gives you one consistent declaration surface across all four.

The platform is a property of each product, not of the tool. Point SchemaQuench at a SQL Server product and it quenches SQL Server. Point it at a MariaDB product and it quenches MariaDB. Same binary, same command line, same CI pipeline shape.

## Four tools, one lifecycle

SchemaSmith is a toolset of four components that cover the full schema lifecycle -- extraction to deployment:

**[SchemaTongs](../reference/schematongs.md)** grips your live database and casts it into a clean, organized package -- tables as JSON, programmable objects as SQL files, everything structured for humans to read and source control to track. Works against SQL Server, PostgreSQL, MySQL, and MariaDB.

**[SchemaQuench](../reference/schemaquench.md)** deploys a schema package to any compatible server -- the moment your declared state hardens into a live database. It reads your declaration, compares it to the target, and applies only the changes needed. No migration ordering. No manual diffing. Run it against dev, staging, and production -- same package, correct results everywhere. Boring, predictable, reliable deployments. That's the goal.

**[DataTongs](../reference/datatongs.md)** grips reference data -- lookup tables, configuration rows, seed data -- and extracts it as deployable synchronization scripts. Capture once, deploy alongside your schema.

**[SchemaShears](../reference/schemashears.md)** carves an object-level patch (subset) package from a full product using a manifest. When you need to deploy only the objects that changed -- without touching everything else -- SchemaShears carves out exactly that slice and stamps the patch so omitted objects are preserved on the target.

## How teams actually use this

A developer needs to add a column to the Orders table. They open `Orders.json`, add the column definition, and submit a pull request. The DBA opens the PR and reviews the table structure directly. Not "ALTER TABLE Orders ADD..." but the full table definition, clear and complete -- the final shape the database should converge to.

Nobody writes ALTER scripts. Nobody maintains migration ordering. Nobody worries about whether the target database matches the assumptions baked into a migration chain. Source control tracks what each table looks like over time, the same way it tracks application code.

The developer thinks in terms of table design. The DBA reviews table design. The tool handles the translation to deployment scripts. You decide what the schema should be. SchemaSmith executes.

## What you get

**A complete, capable toolset for state-based schema management across four database engines -- free.** This is not a stripped-down preview of something bigger. Community contains everything a production team needs to manage schemas as code: conditional deployment with `ShouldApplyExpression`, advanced token tags that embed file contents or live query results into scripts, custom metadata via the `Extensions` carrier, secondary-server support for Availability Groups, custom script folders to fit your deployment lifecycle, per-platform materialized views, PostgreSQL exclude constraints, MySQL multi-column full-text indexes -- the full feature surface. Four engines. One workflow.

Self-contained executables -- download, extract, run. No .NET runtime to install, no dependency chains to manage. Available for Windows, macOS, and Linux on both x64 and ARM64.

Licensed under the SchemaSmith Community License (SSCL v2.0). Manage databases for your own products and services -- any size organization, any revenue, any number of environments, any database size. No usage caps, no nickel-and-diming. What the license does restrict is redistributing SchemaSmith as a standalone product, bundling it inside another product you sell to third parties, or offering it as a hosted or managed service. See the [LICENSE](../../LICENSE) for the full terms. Otherwise: free means free.

Production-tested against real-world schemas. The demo products include Northwind, AdventureWorks, Chinook, and Sakila across all four platforms so you can see it work before pointing it at your own databases.

This is a production-grade schema management ecosystem -- completely free, built by people who've spent decades solving exactly the problems you're facing. The tools are ready. Your databases are waiting.

Have questions about whether SchemaSmith fits your workflow? Wondering how to approach your specific database situation? Forge is here to help -- [ForgeBarrett@SchemaSmith.com](mailto:ForgeBarrett@SchemaSmith.com). Real developers, real answers.

---

Ready to see it in action? [Get started with your first database →](02-quick-start.md)
