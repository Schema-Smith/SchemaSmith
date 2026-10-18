# Datafix Role Grants Reference

Running a datafix through SchemaSmith means deploying under a *scoped* account — one with enough privilege to read, update, and back up data, but no ability to touch schema structure you didn't explicitly authorize. SchemaSmith itself performs no structural DDL under the datafix deployment profile; it executes the migration scripts you provide. Those scripts, however, often need targeted capabilities beyond basic reader/writer access — the most common being `CREATE TABLE` for rollback backup tables. The safe way to grant that is to give the deploy account *its own* schema (`datafix`) to create backup tables in: creating a table in a schema you own needs no rights over the product's own tables, so the account can back up and fix data without any power to alter or drop the schema it's deploying into.

The grant sets below are the recommended starting point for a `datafix_user` account on each supported engine. They are scoped to the minimum required for the Course 6 lab scenario: a price-defect fix across three tenant databases (`shop_tenant_a`, `shop_tenant_b`, `shop_tenant_c`). Treat them as a baseline to tighten per environment — production accounts should carry only the grants that the specific fix has been proven to need.

> **Provisional:** Treat these as the minimal baseline a datafix deploy role needs across all four engines (the Course 6 lab exercises SQL Server, PostgreSQL, and MySQL; MariaDB uses the same grant set as MySQL). Certify them against your own fix and environment — any grant a specific datafix doesn't actually exercise should be removed.

**SchemaSmith's run-once record.** A migration script runs once per database because SchemaQuench records it in a
SchemaSmith table when it has run. The deploy account needs `SELECT`, `INSERT` and `DELETE` on that table and nothing
else of SchemaSmith's. Without them SchemaQuench stops before running the slot, naming the table, rather than run your fix
again on the next deploy.

---

## SQL Server

SQL Server separates server identity from database identity. One `LOGIN` covers authentication at the instance level; a `USER` inside each database maps that login to a database-level principal. You create the login once and repeat the `USER` + `GRANT` block for each tenant.

```sql
-- Server-level login (run against master)
CREATE LOGIN datafix_user WITH PASSWORD = 'DataFix!Demo123';

-- Repeat the block below in each tenant database: shop_tenant_a, shop_tenant_b, shop_tenant_c
USE shop_tenant_a;   -- substitute shop_tenant_b, shop_tenant_c for the other two
GO
CREATE USER datafix_user FOR LOGIN datafix_user;
GO

-- A dedicated schema the deploy user OWNS — backup tables land here
CREATE SCHEMA datafix AUTHORIZATION datafix_user;
GO

-- Reader/writer on the product data (dbo) — note: no structural rights on dbo
GRANT SELECT, INSERT, UPDATE ON SCHEMA::dbo TO datafix_user;

-- Create rollback-backup tables; they land in the owned 'datafix' schema
GRANT CREATE TABLE TO datafix_user;

-- Ancillary stored procedures and functions the fix may call
GRANT EXECUTE ON SCHEMA::dbo TO datafix_user;
-- SchemaSmith's run-once record, so a fix is marked as run and not run again
GRANT SELECT, INSERT, DELETE ON SchemaSmith.CompletedMigrationScripts TO datafix_user;
GO
```

**Why a dedicated `datafix` schema instead of `ALTER ON SCHEMA::dbo`.** `GRANT CREATE TABLE` authorizes the statement, but the new table still has to land *somewhere*. Creating it in `dbo` would additionally require `GRANT ALTER ON SCHEMA::dbo` — and that grant *also* lets the account drop and alter the product's own tables, a structural power a datafix account should never hold. Giving the account its own schema (`CREATE SCHEMA datafix AUTHORIZATION datafix_user`) sidesteps that entirely: it owns the schema, so `CREATE TABLE` there needs no rights over `dbo`. You get exactly the privilege you intend — back up and fix data, nothing structural on the product schema. This is also why it pays to verify what was actually granted: ask a DBA for "rights to create a table" and you may be handed `ALTER ON SCHEMA`, a drop capability in disguise.

**tempdb access** is implicit for any authenticated login; no explicit grant is needed for `#temp` tables.

---

## PostgreSQL

PostgreSQL security is built around cluster-level roles rather than per-database logins. You create one `ROLE` with `LOGIN` privilege, then grant it access to each database. As on SQL Server, the backup table goes in a schema the role *owns* (`datafix`) rather than in `public`: the role reads and writes the existing `public` tables but is given no `CREATE` on `public`, so it can neither add to nor drop the product's own tables. Note also that `GRANT … ON ALL TABLES` only covers tables existing at grant time — another reason the role's own backup table lives in a schema it owns rather than relying on a `public` grant.

```sql
-- Cluster-level role (run connected to postgres or any maintenance database)
CREATE ROLE datafix_user LOGIN PASSWORD 'DataFix!Demo123';

-- Repeat the block below for each tenant database.
-- In psql, use \connect to switch; in a non-interactive context, open a new
-- connection targeting each database.

\connect shop_tenant_a   -- substitute shop_tenant_b, shop_tenant_c for the other two

-- Allow the role to open a connection to this database
GRANT CONNECT   ON DATABASE shop_tenant_a TO datafix_user;

-- Temp space: allows CREATE TEMPORARY TABLE within a session on this database
GRANT TEMPORARY ON DATABASE shop_tenant_a TO datafix_user;

-- Read/write the product data, but no CREATE in public (no structural rights there)
GRANT USAGE ON SCHEMA public TO datafix_user;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO datafix_user;

-- A dedicated schema the deploy user OWNS — backup tables go here
CREATE SCHEMA datafix AUTHORIZATION datafix_user;

-- Ancillary functions and procedures the fix may call
GRANT EXECUTE ON ALL FUNCTIONS  IN SCHEMA public TO datafix_user;
GRANT EXECUTE ON ALL PROCEDURES IN SCHEMA public TO datafix_user;
-- SchemaSmith's run-once record, so a fix is marked as run and not run again
GRANT USAGE ON SCHEMA "SchemaSmith" TO datafix_user;
GRANT SELECT, INSERT, DELETE ON "SchemaSmith"."CompletedMigrationScripts" TO datafix_user;
```

---

## MySQL

In MySQL a schema and a database are the same construct, so there is no separate schema-level grant layer. Tenant isolation maps directly to per-database grants on `shop_tenant_a.*`, `shop_tenant_b.*`, and `shop_tenant_c.*`. The `CREATE` privilege in the `.*` wildcard covers both `CREATE TABLE` (permanent tables) and is paired with `CREATE TEMPORARY TABLES` as a separate privilege for temporary tables.

```sql
-- User account (% means any host; tighten the host specifier in production)
CREATE USER IF NOT EXISTS 'datafix_user'@'%' IDENTIFIED BY 'DataFix!Demo123';

-- Repeat for each tenant database: shop_tenant_a, shop_tenant_b, shop_tenant_c
GRANT SELECT, INSERT, UPDATE, CREATE ON `shop_tenant_a`.* TO 'datafix_user'@'%';
-- SELECT / INSERT / UPDATE: reader/writer on existing data
-- CREATE: backup table creation (schema==database, so no separate schema-level grant needed)

GRANT CREATE TEMPORARY TABLES ON `shop_tenant_a`.* TO 'datafix_user'@'%';
-- Temp space: CREATE TEMPORARY TABLES is a distinct privilege from CREATE in MySQL

GRANT EXECUTE ON `shop_tenant_a`.* TO 'datafix_user'@'%';
-- Ancillary stored procedures and functions the fix may call
GRANT DELETE ON `shop_tenant_a`.`SchemaSmith_CompletedMigrationScripts` TO 'datafix_user'@'%';
-- SchemaSmith's run-once record: SELECT and INSERT come with the grant above

FLUSH PRIVILEGES;
```

---

## Privilege summary across engines

| Capability | SQL Server | PostgreSQL | MySQL | MariaDB |
|---|---|---|---|---|
| Reader/writer on data | `GRANT SELECT, INSERT, UPDATE ON SCHEMA::dbo` | `GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public` | `GRANT SELECT, INSERT, UPDATE ON db.*` | `GRANT SELECT, INSERT, UPDATE ON db.*` |
| Backup table creation | `GRANT CREATE TABLE` + `CREATE SCHEMA datafix AUTHORIZATION datafix_user` (owns the schema) | `CREATE SCHEMA datafix AUTHORIZATION datafix_user` (owns it; no `CREATE` on `public`) | `GRANT CREATE ON db.*` | `GRANT CREATE ON db.*` |
| Temp space | Implicit for authenticated logins | `GRANT TEMPORARY ON DATABASE` | `GRANT CREATE TEMPORARY TABLES ON db.*` | `GRANT CREATE TEMPORARY TABLES ON db.*` |
| Execute ancillary routines | `GRANT EXECUTE ON SCHEMA::dbo` | `GRANT EXECUTE ON ALL FUNCTIONS/PROCEDURES IN SCHEMA public` | `GRANT EXECUTE ON db.*` | `GRANT EXECUTE ON db.*` |
| Run-once record | `GRANT SELECT, INSERT, DELETE ON SchemaSmith.CompletedMigrationScripts` | `GRANT USAGE ON SCHEMA "SchemaSmith"` + `GRANT SELECT, INSERT, DELETE ON "SchemaSmith"."CompletedMigrationScripts"` | `GRANT DELETE ON db.SchemaSmith_CompletedMigrationScripts` (with the reader/writer grant) | as MySQL |
