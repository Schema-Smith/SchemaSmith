# Course 5, Module 4 — Migrating from SSDT/DACPAC (lab)

**SQL Server only.** DACPAC is a SQL Server technology — there's no DACPAC for PostgreSQL or MySQL — so
this module has just a `sqlserver/` folder. That single-engine reality is the point the lesson builds on.

Goal: take a database that an **SSDT project** built and published (a `.dacpac` deployed with SqlPackage)
and move it to SchemaSmith. You're already declarative, so this isn't about giving up migration scripts —
it's about dropping the build-and-publish step and gaining one workflow that still works when you later
add another engine. You'll cast the live database to declarative files and quench to a clean no-op.

You do **not** build or publish the SSDT project. The `before/` folder shows a real SSDT source
(`.sqlproj`, object-per-file `Tables/*.sql`, a publish profile) for reference; the setup already applied
its end state to `shop_from_dacpac`.

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and verified (SQL Server `PASS`).
- The Course 5 databases exist — run [`../course5-setup`](../course5-setup) once (creates and seeds
  `shop_from_dacpac`, among others).
- The CLI is on your PATH (`schematongs --version` and `schemaquench --version` answer **2.7.0 or later**). New to the
  CLI? Course 1, Module 1 walks the install.

The `sqlserver/` folder ships a `SchemaTongs.settings.json` (the extract config), a `quench.settings.json`
(the deploy config), and the `Package/` this lab produced — so you can diff your own extract against it.

## Step 1: Look at the SSDT source

```bash
ls before/Tables/
# → Customer.sql  Product.sql  SalesOrder.sql  OrderItem.sql
```

Object-per-file `CREATE TABLE` scripts, a `.sqlproj` that builds them into a `.dacpac`, and a
`ShopDb.publish.xml` profile. There's no runtime history table to walk away from — DACPAC doesn't keep
one. You're already declarative; the move is about the tooling around these files, not the files' shape.

## Step 2: Extract — cast the live database

Even though you already have `.sql` files, the cleanest baseline is a cast of the live database your
DACPAC published — it's guaranteed to match what's deployed. The whitelist names your four tables:

```json
"ShouldCast": { "ObjectList": "dbo.Customer,dbo.Product,dbo.SalesOrder,dbo.OrderItem" }
```

```bash
cd sqlserver
schematongs --ConfigFile:SchemaTongs.settings.json
ls Package/Templates/Main/Tables/
```

```
=== Casting Summary ===
  Tables:     4 extracted, 0 errors

dbo.Customer.json  dbo.OrderItem.json  dbo.Product.json  dbo.SalesOrder.json
```

## Step 3: Quench — adopt, then prove the no-op

```bash
schemaquench --ConfigFile:quench.settings.json
```

The first run adopts your existing tables (stamps them as managed, stands up SchemaSmith's own
bookkeeping in a separate `SchemaSmith` schema). Run it a second time and nothing happens — a clean
no-op. That no-op is the proof: the package matches the live database your DACPAC built. Your publish
profile's `DropObjectsNotInSource` switch becomes `DropTablesRemovedFromProduct` in the quench config
(default `true`); for the data-loss protection `BlockOnPossibleDataLoss` gave you, set that flag `false`
(never drop) or install the recyclebin hooks (drop-but-recoverable), with WhatIf to preview first. And
there's no `.dacpac` build between commit and deploy.

## The principle

SSDT already made you declarative — that's the hard part, and you've done it. SchemaSmith takes the build
step out (the files you commit are what deploys) and replaces the SqlPackage/Visual Studio stack with one
CLI. The bigger payoff shows up later: when your shop adds PostgreSQL or MySQL, DACPAC can't follow, but
the SchemaSmith workflow can. Packages stay native per engine — a SQL Server package is its own thing —
but the extract-and-quench workflow is identical on every engine. One toolset, one mental model, however
many engines you end up running.
