# Course 9 · Module 1 — Same change, three engines

One class of change. Three native, separate, independently-deployable packages. The workflow is identical on each engine — the DDL is not.

That is the parity spine: you run the same two commands per service (baseline deploy, then after deploy), you get the same three-part change (nullable column, index on it, reference table with a seed), and each service lands in its own native dialect without any of the others knowing about it. Three commands. Three services. One rhythm.

## What the change is

Every service gets the identical class of change in its own native form:

| Engine | Column added | Index | Reference table | Seed |
| --- | --- | --- | --- | --- |
| SQL Server (`orders`) | `[Phone]` NVARCHAR(30) NULL on `[Customer]` | `[IX_Customer_Phone]` | `[OrderStatus]` | 4 rows (NEW / PAID / SHIPPED / CANCELLED) |
| PostgreSQL (`catalog`) | `brand` text NULL on `product` | `ix_product_brand` | `product_status` | 3 rows (ACTIVE / DISCONTINUED / DRAFT) |
| MySQL (`sessions`) | `` `DeviceModel` `` varchar(60) NULL on `` `Session` `` | `IX_Session_DeviceModel` | `` `EventCategory` `` | 4 rows (PAGE_VIEW / ADD_TO_CART / CHECKOUT / LOGOUT) |

Each package is deployed on its own — the orders package never touches the sessions database, and vice versa. That independent deployability is the point. In a real polyglot environment each service owns its own pipeline; SchemaSmith treats them the same way.

## Prerequisites

- Three-engine sandbox is up (`Demos/Learn/docker`).
- **Run course9-setup first** ([`../course9-setup/README.md`](../course9-setup/README.md)) so the `orders`, `catalog`, and `sessions` databases exist. This module deploys into those databases — it does not create them.
- `schemaquench --version` answers **2.7.0** or later.

## Steps

Work through each service in order. Each is self-contained — you can deploy them in any order and each exits 0 independently.

---

### Orders — SQL Server

```
cd sqlserver
```

**Baseline deploy:**

macOS / Linux:
```
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD/logs"
```

Windows:
```
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD\logs"
```

Exit 0. The `Orders` schema is deployed to `orders` with `[Customer]`, `[SalesOrder]`, and `[OrderItem]`.

**After deploy:**

macOS / Linux:
```
schemaquench --ConfigFile:quench.settings.after.json --LogPath:"$PWD/logs"
```

Windows:
```
schemaquench --ConfigFile:quench.settings.after.json --LogPath:"$PWD\logs"
```

Exit 0. `[Customer]` gains `[Phone]` NVARCHAR(30) NULL and index `[IX_Customer_Phone]`. The new `[OrderStatus]` reference table is created and seeded with four status codes. SchemaSmith writes bracket-quoted T-SQL throughout — native SQL Server dialect.

---

### Catalog — PostgreSQL

```
cd postgres
```

**Baseline deploy:**

macOS / Linux:
```
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD/logs"
```

Windows:
```
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD\logs"
```

Exit 0. The `Catalog` schema is deployed to `catalog` with `category` and `product`.

**After deploy:**

macOS / Linux:
```
schemaquench --ConfigFile:quench.settings.after.json --LogPath:"$PWD/logs"
```

Windows:
```
schemaquench --ConfigFile:quench.settings.after.json --LogPath:"$PWD\logs"
```

Exit 0. `product` gains `brand` text NULL and index `ix_product_brand`. The new `product_status` reference table is created and seeded with three status codes using `ON CONFLICT (code) DO NOTHING` — idiomatic PostgreSQL. No brackets, no backticks: lowercase unquoted names throughout.

---

### Sessions — MySQL

```
cd mysql
```

**Baseline deploy:**

macOS / Linux:
```
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD/logs"
```

Windows:
```
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD\logs"
```

Exit 0. The `Sessions` schema is deployed to `sessions` with `` `Session` `` and `` `Event` ``.

**After deploy:**

macOS / Linux:
```
schemaquench --ConfigFile:quench.settings.after.json --LogPath:"$PWD/logs"
```

Windows:
```
schemaquench --ConfigFile:quench.settings.after.json --LogPath:"$PWD\logs"
```

Exit 0. `` `Session` `` gains `` `DeviceModel` `` varchar(60) NULL (utf8mb4) and index `IX_Session_DeviceModel`. The new `` `EventCategory` `` reference table is created and seeded with four category codes using `INSERT IGNORE` — idiomatic MySQL. Backtick-quoted names throughout.

---

## What to notice

After the three after-deploys finish, look at what SchemaSmith wrote. Open `artifacts/` in each engine folder and compare the index DDL — the change was described once per package (one JSON entry per column, one index entry) and the engine-native DDL fell out on the other side. That is SchemaSmith's core proposition for polyglot environments: the authoring model is consistent, the output is native.

Each service was also deployed independently — three `schemaquench` commands, each targeting one database, each exiting cleanly without knowing the others exist. In a real pipeline you would run the SQL Server, PostgreSQL, and MySQL deploys in parallel pipelines that share nothing. SchemaSmith supports that naturally because the package boundary is the service boundary.

## What each folder is

| Path | Purpose |
| --- | --- |
| `sqlserver/baseline/` | Orders schema — Customer, SalesOrder, OrderItem — deployed to `orders`. |
| `sqlserver/after/` | Baseline + Phone column, IX_Customer_Phone, and OrderStatus reference table with seed. |
| `postgres/baseline/` | Catalog schema — category, product — deployed to `catalog`. |
| `postgres/after/` | Baseline + brand column, ix_product_brand, and product_status reference table with seed. |
| `mysql/baseline/` | Sessions schema — Session, Event — deployed to `sessions`. |
| `mysql/after/` | Baseline + DeviceModel column, IX_Session_DeviceModel, and EventCategory reference table with seed. |
| `<engine>/quench.settings.baseline.json` | Points at the baseline package, targets the engine's service database. |
| `<engine>/quench.settings.after.json` | Points at the after package, same target. |

## Up next

Module 2 is where the parity spine shows its limits: keys and types that are native to one engine have no direct equivalent on another. The identical-workflow assumption holds — the DDL does not.
