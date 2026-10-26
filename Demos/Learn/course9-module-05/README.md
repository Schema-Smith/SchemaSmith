# Course 9 · Module 5 — Ready to replicate

<!-- TRAINING-RELEASE-PIN 2.8.0 (#595 converging publication scripts; creating a policy is logged): delete the "merged, not yet released" note -->

Two PostgreSQL databases with identical schemas can disagree about whether a write is allowed. Publish a table with no
usable **replica identity** and PostgreSQL refuses every `UPDATE` and `DELETE` on it. Turn on **row-level security**
with no policy and the table returns nothing to anyone but its owner. Neither shows up in a column list, and both are
declared state. This module declares them, then publishes the tables and proves a row really arrives at a subscriber.

PostgreSQL only: `postgres/`, against `catalog_repl` on the sandbox (port 15432) and on a second, subscriber
PostgreSQL (port 15434).

> **Merged, not yet released.** This module teaches SchemaSmith 2.8 behaviour, which is merged to `main` and not yet
> in a tagged release.

## Before you start

- The [sandbox](../docker) is up. Its PostgreSQL runs with `wal_level=logical`, which logical replication needs. If you
  started the sandbox before this module existed, run `docker compose up -d` in `../docker` once (PostgreSQL keeps its
  data).
- The CLI is on your PATH: `schemaquench --version` answers **2.8.0 or later**.

Create the database and the role the policy will name:

```bash
# from course9-module-05
bash -c '. ../lab-sql.sh && lab_confirm_db postgres catalog_repl'
../lab-sql.sh postgres learn "DO \$\$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pub_reader') THEN CREATE ROLE pub_reader; END IF; END \$\$"
```

## Step 1: Identical schema, refused write

The package has three tables. `audit_event` has a unique index but no primary key. [`billing_pub.sql`](postgres/Package/Templates/Main/Publications/billing_pub.sql)
publishes `invoice` and `audit_event`:

```sql
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'billing_pub') THEN
    CREATE PUBLICATION billing_pub FOR TABLE public.invoice, public.audit_event;
  END IF;
END
$$;
```

```bash
# from course9-module-05/postgres
schemaquench --ConfigFile:quench.settings.json        # exit 0
```

```bash
# from course9-module-05
../lab-sql.sh postgres catalog_repl "INSERT INTO public.audit_event VALUES (1, 'opened')"
../lab-sql.sh postgres catalog_repl "UPDATE public.audit_event SET note = 'edited' WHERE event_id = 1"     # refused
#   ERROR:  cannot update table "audit_event" because it does not have a replica identity and publishes updates
#   HINT:  To enable updating the table, set REPLICA IDENTITY using ALTER TABLE.
```

The update fails with a non-zero exit, and that's the lesson. A published table has to tell subscribers which row an
update or delete is about. With no primary key, `audit_event`'s default replica identity identifies nothing, so
PostgreSQL won't take the write. An unpublished copy of the same table would take it without complaint.

## Step 2: Declare the replica identity

Give `audit_event` the bluntest identity first, `FULL`: the whole old row identifies it.

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Tables/public.audit_event.json" set ReplicaIdentity '"FULL"' -->
```json
"ReplicaIdentity": "FULL"
```

```bash
# from course9-module-05/postgres
schemaquench --ConfigFile:quench.settings.json        # exit 0
#         Setting replica identity for public.audit_event
```

```bash
# from course9-module-05
../lab-sql.sh postgres catalog_repl "UPDATE public.audit_event SET note = 'edited' WHERE event_id = 1"
# → UPDATE 1
../lab-sql.sh postgres catalog_repl "SELECT relreplident FROM pg_class WHERE relname = 'audit_event'"
# → f
```

`FULL` writes the whole old row to the WAL on every update. The unique index over `event_id` (`NOT NULL`) is a cheaper
identity:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Tables/public.audit_event.json" set ReplicaIdentity '"INDEX"' -->
<!-- walk: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Tables/public.audit_event.json" set ReplicaIdentityIndex '"ux_audit_event_id"' -->
```json
"ReplicaIdentity": "INDEX",
"ReplicaIdentityIndex": "ux_audit_event_id"
```

```bash
# from course9-module-05/postgres
schemaquench --ConfigFile:quench.settings.json        # exit 0
#         Setting replica identity for public.audit_event
```

```bash
# from course9-module-05
../lab-sql.sh postgres catalog_repl "SELECT relreplident FROM pg_class WHERE relname = 'audit_event'"
# → i
```

Deploy again: exit 0, and no replica-identity line. It matches.

## Step 3: Row-level security, with and without a policy

Load two tenants' invoices and let `pub_reader` read the table:

```bash
# from course9-module-05
../lab-sql.sh postgres catalog_repl "INSERT INTO public.invoice VALUES (1, 7, 100.00), (2, 8, 200.00); GRANT SELECT ON public.invoice TO pub_reader"
```

Turn row-level security on, with no policy yet:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Tables/public.invoice.json" set RowLevelSecurity true -->
```json
"RowLevelSecurity": true
```

```bash
# from course9-module-05/postgres
schemaquench --ConfigFile:quench.settings.json        # exit 0
```

```bash
# from course9-module-05
../lab-sql.sh postgres catalog_repl "SET ROLE pub_reader; SELECT COUNT(*) FROM public.invoice"
# → 0
```

The grant says `pub_reader` may read the table. Row-level security with no policy says it may read no rows. Add a policy
that shows each tenant its own:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Tables/public.invoice.json" set Policies '[{"Name": "tenant_read", "Command": "SELECT", "Roles": "pub_reader", "UsingExpression": "tenant_id = current_setting('"'"'app.tenant_id'"'"')::int"}]' -->
```json
"Policies": [
  {
    "Name": "tenant_read",
    "Command": "SELECT",
    "Roles": "pub_reader",
    "UsingExpression": "tenant_id = current_setting('app.tenant_id')::int"
  }
]
```

```bash
# from course9-module-05/postgres
schemaquench --ConfigFile:quench.settings.json        # exit 0
#         Creating policy public.invoice.tenant_read
```

```bash
# from course9-module-05
../lab-sql.sh postgres catalog_repl "SET ROLE pub_reader; SET app.tenant_id = '7'; SELECT invoice_id FROM public.invoice"
# → 1
```

Now tighten it. Add `AND amount < 1000` to the `UsingExpression`:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Tables/public.invoice.json" set "Policies[Name=tenant_read].UsingExpression" '"tenant_id = current_setting('"'"'app.tenant_id'"'"')::int AND amount < 1000"' -->
```bash
# from course9-module-05/postgres
schemaquench --ConfigFile:quench.settings.json        # exit 0
#         Altering policy public.invoice.tenant_read
```

`ALTER POLICY`, in place: there's never a moment when the table has row-level security on and the rule missing. Deploy
again and nothing happens, even though PostgreSQL stores the expression in its own form. SchemaSmith compares
expressions, not text.

## Step 4: A publication script that keeps up

Add `refund` to the publication by editing the script you wrote in Step 1:

<!-- walk: sed -i 's/FOR TABLE public.invoice, public.audit_event;/FOR TABLE public.invoice, public.audit_event, public.refund;/' postgres/Package/Templates/Main/Publications/billing_pub.sql -->
```sql
    CREATE PUBLICATION billing_pub FOR TABLE public.invoice, public.audit_event, public.refund;
```

```bash
# from course9-module-05/postgres
schemaquench --ConfigFile:quench.settings.json        # exit 0
#     Quenched .\Package\Templates\Main\Publications\billing_pub.sql
```

```bash
# from course9-module-05
../lab-sql.sh postgres catalog_repl "SELECT tablename FROM pg_publication_tables WHERE pubname = 'billing_pub' ORDER BY tablename"
# → audit_event
# → invoice
```

The script ran, and `refund` isn't published. `IF NOT EXISTS` made it safe to re-run and also made it ignore every
edit: once the publication exists, the `CREATE` never runs again. (Course 4 Recipe 11 shows the same trap with domain
types.) A publication script has to converge. [`converging/billing_pub.sql`](postgres/converging/billing_pub.sql)
keeps the guarded create for a fresh database and then says what the publication publishes, every time:

```sql
ALTER PUBLICATION billing_pub SET TABLE public.invoice, public.audit_event, public.refund;
```

```bash
# from course9-module-05/postgres
cp converging/billing_pub.sql Package/Templates/Main/Publications/billing_pub.sql
schemaquench --ConfigFile:quench.settings.json        # exit 0
```

```bash
# from course9-module-05
../lab-sql.sh postgres catalog_repl "SELECT tablename FROM pg_publication_tables WHERE pubname = 'billing_pub' ORDER BY tablename"
# → audit_event
# → invoice
# → refund
```

You don't have to remember that form. SchemaTongs extracts a publication the same way, as a guarded `CREATE` followed by
`ALTER PUBLICATION … SET` for its tables and its options (Step 6).

## Step 5: A real subscriber

Start the `replica` profile's PostgreSQL and give it the same database and role:

```bash
# from docker
docker compose --profile replica up -d postgres-replica
```

```bash
# from course9-module-05
LEARN_CONTAINER=learn-postgres-replica bash -c '. ../lab-sql.sh && lab_confirm_db postgres catalog_repl'
LEARN_CONTAINER=learn-postgres-replica ../lab-sql.sh postgres learn "DO \$\$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pub_reader') THEN CREATE ROLE pub_reader; END IF; END \$\$"
```

A subscriber needs the tables to exist. Logical replication copies rows, not schema, so deploy the same package to it
with [`quench.settings.replica.json`](postgres/quench.settings.replica.json) (port 15434):

```bash
# from course9-module-05/postgres
schemaquench --ConfigFile:quench.settings.replica.json        # exit 0
```

The deploy also creates `billing_pub` on the subscriber, where nothing uses it. Now subscribe. The subscriber reaches
the publisher as host `postgres` on the sandbox's own network:

```bash
# from course9-module-05
LEARN_CONTAINER=learn-postgres-replica ../lab-sql.sh postgres catalog_repl "CREATE SUBSCRIPTION billing_sub CONNECTION 'host=postgres port=5432 dbname=catalog_repl user=postgres password=Learn!Passw0rd' PUBLICATION billing_pub"
LEARN_CONTAINER=learn-postgres-replica ../lab-sql.sh postgres catalog_repl "SELECT pg_sleep(5); SELECT event_id, note FROM public.audit_event ORDER BY event_id"
# → 1|edited
```

The initial sync copied what was there. Now change the publisher and watch it arrive:

```bash
# from course9-module-05
../lab-sql.sh postgres catalog_repl "INSERT INTO public.refund VALUES (1, 1, 25.00); UPDATE public.audit_event SET note = 'replicated' WHERE event_id = 1"
LEARN_CONTAINER=learn-postgres-replica ../lab-sql.sh postgres catalog_repl "SELECT pg_sleep(3); SELECT refund_id, amount FROM public.refund"
# → 1|25.00
LEARN_CONTAINER=learn-postgres-replica ../lab-sql.sh postgres catalog_repl "SELECT note FROM public.audit_event WHERE event_id = 1"
# → replicated
```

The update reached the subscriber identified by `ux_audit_event_id`, the replica identity you declared in Step 2.

## Step 6: Extract it

```bash
# from course9-module-05/postgres
schematongs --ConfigFile:SchemaTongs.settings.json        # exit 0
```

`extracted/Templates/Main/Tables/audit_event.json` carries `"ReplicaIdentity": "INDEX"` and
`"ReplicaIdentityIndex": "ux_audit_event_id"`. `invoice.json` carries `"RowLevelSecurity": true` and the policy, its
expression in PostgreSQL's own rendering, which redeploys unchanged. `Publications/billing_pub.sql` is the converging
form, with the publish options spelled out.

## What you proved

- Replica identity decides whether a published table takes updates and deletes, and it's declared: `FULL`, or `INDEX`
  over a unique index.
- Row-level security without a policy hides every row. A policy is declared, created, and edited in place.
- A publication script has to converge, and an extracted one does.
- A row written on the publisher arrives on a subscriber whose tables came from the same package.

## Clean up

```bash
# from course9-module-05
LEARN_CONTAINER=learn-postgres-replica ../lab-sql.sh postgres catalog_repl "DROP SUBSCRIPTION billing_sub"
rm -rf postgres/extracted
bash -c '. ../lab-sql.sh && lab_remove_db postgres catalog_repl'
```

```bash
# from docker
docker compose rm -sfv postgres-replica
```

Put the package back with `git checkout -- postgres` if you want to run the module again.
