# Course 12, Module 11 — The security layer, declared (lab)

Goal: bring the objects that guard a database into the package: a **row-level security policy** and its predicate
function, **Always Encrypted** key metadata and an encrypted column, a **certificate-signed** procedure, and triggers
whose **disabled state and firing order** matter. You'll build them by hand the way a DBA did, extract them, deploy
the package back, change the signed procedure, and then deploy the whole layer into a database that has never seen
it.

SQL Server only: `sqlserver/`, against `declared_m11` and a fresh `declared_m11_copy`.

## Before you start

- The [sandbox](../docker) is up and the Course 12 databases exist (run [`../course12-setup`](../course12-setup)
  once — it creates `declared_m11`).
- The CLI is on your PATH: `schemaquench --version` and `schematongs --version` answer **2.8.0 or later**.
- SQL Server **2016 or later** for row-level security and Always Encrypted (the sandbox is 2022).

## Step 1: The layer somebody built by hand

[`security-layer.sql`](sqlserver/security-layer.sql) is what years of tickets left in `declared_m11`:

| Object | What it does |
| --- | --- |
| `Security.RegionPolicy` | A filter predicate: a session sees only the `Orders` rows for the region in its `SESSION_CONTEXT` |
| `CustomerCMK`, `CustomerCEK` | Always Encrypted key metadata; `Customer.SSN` is encrypted with them |
| `dbo.usp_RegionTotals` | Signed with the certificate `ReportSigning`, so it can be granted rights its callers don't have |
| `trg_Orders_Stamp` | Set to fire **first** on insert |
| `trg_Orders_Legacy` | **Disabled**, kept for reference |

```bash
# from course12-module-11
../lab-sql.sh sqlserver declared_m11 --file sqlserver/security-layer.sql
../lab-sql.sh sqlserver declared_m11 "EXEC sp_set_session_context N'Region', N'EU'; SELECT COUNT(*) FROM dbo.Orders"
# → 2
```

Three orders, two of them in the EU: the policy is doing its job.

## Step 2: Extract it

```bash
# from course12-module-11/sqlserver
schematongs --ConfigFile:SchemaTongs.settings.json
```

```
  Tables:     3 extracted, 0 errors
  Schemas:    1 extracted
  Functions:  1 extracted
  Procedures: 1 extracted
  Triggers:   3 extracted
  Security policies: 1 extracted
```

Look at what came out:

```bash
# from course12-module-11/sqlserver
ls Package/Templates/Main/SecurityPolicies Package/Templates/Main/Prerequisites
tail -3 Package/Templates/Main/Triggers/dbo.Orders.trg_Orders_Legacy.sql
tail -2 Package/Templates/Main/Triggers/dbo.Orders.trg_Orders_Stamp.sql
```

```
DISABLE TRIGGER [dbo].[trg_Orders_Legacy] ON [dbo].[Orders]
EXEC sp_settriggerorder @triggername = N'[dbo].[trg_Orders_Stamp]', @order = N'First', @stmttype = N'INSERT'
```

- The policy script compares the live policy with the extracted one and re-creates it only when they differ, inside a
  transaction.
- `Prerequisites/AlwaysEncryptedKeys.sql` creates each key only where it's missing. It carries the key's metadata,
  never key material: `KEY_PATH` names a certificate outside the database.
- A trigger's disabled state and its firing order aren't part of its definition, so each trigger script ends with the
  batch that puts them back. Without that tail, every deploy would re-enable the legacy trigger and unorder the
  first one.
- The procedure is extracted **without** its signature. A signature needs the certificate's private key, and that
  can't be read out of a database, so it can't be scripted.

## Step 3: Deploy the package back

```bash
# from course12-module-11/sqlserver
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

Two lines to read. The procedure script runs, which strips its signature, so SchemaQuench signs it again with the
same certificate:

```
Signing [dbo].[usp_RegionTotals] again
```

and the summary records it as `resigned`. It can do that because the certificate's private key is protected by the
database master key, which the server opens without a password.

The other: `Dropping security policy Security.RegionPolicy: it is bound to Security.fn_RegionFilter…`. A
schema-bound predicate function can't be altered while a policy uses it, so on a deploy that runs the function's
script, the policy comes off first and the policy script puts it back a moment later. That happens on the first
deploy and whenever the function changes; plan those like any change to the policy itself.

```bash
# from course12-module-11
../lab-sql.sh sqlserver declared_m11 "SELECT CONCAT('signed=', (SELECT COUNT(*) FROM sys.crypt_properties WHERE major_id = OBJECT_ID('dbo.usp_RegionTotals')), ' legacy_disabled=', (SELECT is_disabled FROM sys.triggers WHERE name = 'trg_Orders_Legacy'), ' stamp_first=', OBJECTPROPERTY(OBJECT_ID('dbo.trg_Orders_Stamp'), 'ExecIsFirstInsertTrigger'))"
# → signed=1 legacy_disabled=1 stamp_first=1
```

Still signed, still disabled, still first.

## Step 4: Change the signed procedure

Sort the totals. In `Package/Templates/Main/Procedures/dbo.usp_RegionTotals.sql`, add `ORDER BY Region`:

<!-- walk: sed -i 's/GROUP BY Region;/GROUP BY Region ORDER BY Region;/' "sqlserver/Package/Templates/Main/Procedures/dbo.usp_RegionTotals.sql" -->
```text
SELECT Region, SUM(Amount) AS Total FROM dbo.Orders GROUP BY Region ORDER BY Region;
```

```bash
# from course12-module-11/sqlserver
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
grep -A1 '"objectName": "\[dbo\].\[usp_RegionTotals\]"' "logs/SchemaQuench - Summary.json"
```

The `ALTER` dropped the signature and the deploy put it back: the summary row says `resigned`. The other procedure, function
and trigger scripts are `unchanged` and aren't run at all, and a module the deploy doesn't alter keeps its signature
untouched.

## Step 5: Deploy the layer to a database that has never seen it

```bash
# from course12-module-11
../lab-sql.sh sqlserver master "IF DB_ID('declared_m11_copy') IS NULL CREATE DATABASE declared_m11_copy"
```

`quench.settings.copy.json` points the same package at it through the `MainDb` token:

```bash
# from course12-module-11/sqlserver
schemaquench --ConfigFile:quench.settings.copy.json --LogPath:"$PWD/logs"
```

```bash
# from course12-module-11
../lab-sql.sh sqlserver declared_m11_copy "SELECT CONCAT('policies=', (SELECT COUNT(*) FROM sys.security_policies), ' keys=', (SELECT COUNT(*) FROM sys.column_encryption_keys), ' encrypted_columns=', (SELECT COUNT(*) FROM sys.columns WHERE encryption_type IS NOT NULL), ' signed=', (SELECT COUNT(*) FROM sys.crypt_properties WHERE major_id = OBJECT_ID('dbo.usp_RegionTotals')), ' legacy_disabled=', (SELECT is_disabled FROM sys.triggers WHERE name = 'trg_Orders_Legacy'))"
# → policies=1 keys=1 encrypted_columns=1 signed=0 legacy_disabled=1
```

The policy, the keys before the encrypted column that needs them, the disabled trigger: all there. The procedure is
there **unsigned**, and nothing warns, because there was never a signature on it here to lose. Signing belongs to
each environment: create or restore the certificate there, and sign with an `ADD SIGNATURE` in a migration script.
From then on, deploys keep the signature on.

## Step 6: Clean up

```bash
# from course12-module-11
../lab-sql.sh sqlserver master "IF DB_ID('declared_m11_copy') IS NOT NULL BEGIN ALTER DATABASE declared_m11_copy SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE declared_m11_copy END"
```

## The principle

The objects that guard a database are the ones you can least afford to recreate from memory, and most of them can be
declared. Extract them, and every deploy compares the policy, keeps the keys ahead of the columns that need them,
leaves a disabled trigger disabled and an ordered one first, and re-signs what it has to alter. The one thing that
can't travel is a private key, so a signature is set up once per environment and kept from then on.
