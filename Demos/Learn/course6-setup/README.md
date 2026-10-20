# Course 6 — Database Setup

These scripts create and **seed** the Course 6 datafix databases on the sandbox engines — 12 databases
in total. Each tenant database carries the identical Shop schema (`Customer`, `Product`, `SalesOrder`,
`OrderItem`) and a **deterministic price-defect batch**: `OrderItem` rows on orders placed in May 2026
store `UnitPrice = ROUND(Product.UnitPrice * 0.81, 2)` — a 10% discount applied twice (the bug).
Orders from April and June 2026 carry the intended `ROUND(Product.UnitPrice * 0.90, 2)`.

The three tenant databases start equal so that Course 6 labs can canary the fix to `shop_tenant_a`
first before rolling it out to `shop_tenant_b` and `shop_tenant_c`.

The setup also creates a scoped **`datafix_user`** account on each engine — a least-privilege login
that owns a dedicated `datafix` schema for its backup tables but has no power to alter or drop the
product's own tables. The Module 1 lab deploys *as* this account. The grant scripts live in
[`seed/<engine>/datafix_role.sql`](seed/) and are explained in the
[datafix-role grants reference](../../../docs/end-user/reference/datafix-role-grants.md).

## Prerequisite

The shared sandbox must be running. See [`Demos/Learn/README.md`](../README.md) for how to start it
and verify it is healthy before continuing.

## Run the setup

**macOS / Linux**

```bash
# from course6-setup
bash setup-databases.sh
```

**Windows (PowerShell)**

```powershell
# from course6-setup
.\setup-databases.ps1
```

Both scripts print `PASS` or `FAIL` for each database (`PASS` only after a shop table is confirmed):

```text
SQL Server
  shop_tenant_a              PASS
  shop_tenant_b              PASS
  shop_tenant_c              PASS
  datafix_user role          PASS
PostgreSQL
  shop_tenant_a              PASS
  shop_tenant_b              PASS
  shop_tenant_c              PASS
  datafix_user role          PASS
MySQL
  shop_tenant_a              PASS
  shop_tenant_b              PASS
  shop_tenant_c              PASS
  datafix_user role          PASS
MariaDB
  shop_tenant_a              PASS
  shop_tenant_b              PASS
  shop_tenant_c              PASS
  datafix_user role          PASS

All 12 databases are seeded and the datafix_user role is created (3 SQL Server, 3 PostgreSQL, 3 MySQL, 3 MariaDB).
```

## Databases created

| Database        | Contains |
| --------------- | -------- |
| `shop_tenant_a` | Shop schema + price-defect batch (canary target in later labs) |
| `shop_tenant_b` | Shop schema + price-defect batch (identical to `a`) |
| `shop_tenant_c` | Shop schema + price-defect batch (identical to `a`) |

All three tenant databases are created on all four engines (SQL Server, PostgreSQL, MySQL, MariaDB).

## Connection details

These are throwaway sandbox credentials — **never reuse them anywhere real.**

| Engine     | Host        | Port    | User       | Password         |
| ---------- | ----------- | ------- | ---------- | ---------------- |
| SQL Server | `localhost` | `11433` | `sa`       | `Learn!Passw0rd` |
| PostgreSQL | `localhost` | `15432` | `postgres` | `Learn!Passw0rd` |
| MySQL      | `localhost` | `13306` | `root`     | `Learn!Passw0rd` |
| MariaDB    | `localhost` | `13307` | `root`     | `Learn!Passw0rd` |

The admin accounts above are for seeding and inspection. The Module 1 lab deploys *as* the scoped
`datafix_user` account (password `DataFix!Demo123`) created by the setup — that's the whole point of
the exercise.

## Verifying the bad batch

After running the setup, confirm the defect is present and equal across all three tenants.

Each line counts the bad batch in all three tenants on one engine (MariaDB runs the same query as MySQL):

```bash
# from course6-setup
for db in shop_tenant_a shop_tenant_b shop_tenant_c; do ../lab-sql.sh sqlserver "$db" "SELECT COUNT(*) AS bad_batch_rows FROM dbo.OrderItem oi JOIN dbo.SalesOrder so ON so.OrderId = oi.OrderId WHERE so.OrderDate >= '2026-05-01' AND so.OrderDate < '2026-06-01'"; done
for db in shop_tenant_a shop_tenant_b shop_tenant_c; do ../lab-sql.sh postgres "$db" "SELECT COUNT(*) AS bad_batch_rows FROM orderitem oi JOIN salesorder so ON so.orderid = oi.orderid WHERE so.orderdate >= '2026-05-01' AND so.orderdate < '2026-06-01'"; done
for db in shop_tenant_a shop_tenant_b shop_tenant_c; do ../lab-sql.sh mysql "$db" "SELECT COUNT(*) AS bad_batch_rows FROM OrderItem oi JOIN SalesOrder so ON so.OrderId = oi.OrderId WHERE so.OrderDate >= '2026-05-01' AND so.OrderDate < '2026-06-01'"; done
for db in shop_tenant_a shop_tenant_b shop_tenant_c; do ../lab-sql.sh mariadb "$db" "SELECT COUNT(*) AS bad_batch_rows FROM OrderItem oi JOIN SalesOrder so ON so.OrderId = oi.OrderId WHERE so.OrderDate >= '2026-05-01' AND so.OrderDate < '2026-06-01'"; done
```

Expected result: **10 rows** in every tenant on every engine (5 May-2026 orders × 2 items each,
with orders 105–109 carrying 10 affected `OrderItem` rows in total).

## Re-running is safe

The seed scripts are idempotent — every table is dropped and recreated, so a second run restores the
exact starting state and still reports `PASS` for every database. Run them as often as you like; if a
lab leaves a database in an odd state, re-run the setup to reset it.

## Starting over: `-Reset`

Re-running the setup already restores each table to its starting state, but the database itself is
never touched. `-Reset` goes a step further — it drops and recreates the three tenant databases
before reseeding, so the price-defect batch is exactly as described above again on all three
tenants. The `datafix_user` role isn't dropped; it's simply reconfirmed against the freshly reseeded
databases (grants are idempotent):

```bash
# from course6-setup
bash setup-databases.sh --reset
```

```powershell
# from course6-setup
.\setup-databases.ps1 -Reset
```

Each tenant database is dropped and recreated, then reseeded, reported as `PASS (reset)`. **Only
databases these scripts created are ever dropped.** On your own server, a database carrying one of
these names that the labs didn't create is refused and left untouched — you'll be told to rename or
move it. Nothing of yours is at risk.
