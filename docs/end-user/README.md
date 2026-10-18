# SchemaSmith Documentation

Welcome to the shop. Everything you need to master SchemaSmith is here -- the guide walks you through it front to back, and the reference docs are always within arm's reach when you need the details. Pick a starting point and dig in.

SchemaSmith supports **SQL Server**, **PostgreSQL**, **MySQL**, and **MariaDB** as first-class peers. The same tools, the same schema package format, the same workflow -- pointed at whichever engine your team runs.

---

## Start Here

New to SchemaSmith? The guide takes you from "what is this?" to confidently managing databases. Start at the beginning and follow the thread:

**[Why SchemaSmith →](guide/01-why-schemasmith.md)**

---

## Get the Tools

Before the walkthrough, install SchemaSmith. One command per platform — Chocolatey on Windows, `.deb` / `.rpm` / `install.sh` on Linux and macOS. No .NET runtime to install.

**[Installation →](guide/installation.md)**

---

## The Guide

A hands-on journey from first contact to confident production deployment, organized around three pillars: **Shape** your schema, **Strengthen** your process, **Succeed** in production.

### Prologue

| Chapter | What you'll learn |
|---------|-------------------|
| [1. Why SchemaSmith](guide/01-why-schemasmith.md) | The problem, the vision, and why state-based changes everything |
| [2. Quick Start](guide/02-quick-start.md) | Extract, view, deploy, and change a database in under 15 minutes |

### Shape -- Give your database form

| Chapter | What you'll learn |
|---------|-------------------|
| [3. Core Concepts](guide/03-core-concepts.md) | Products, templates, schema packages, and the deployment model |
| [4. Defining Your Schema](guide/04-defining-your-schema.md) | Adding tables, modifying schemas, extraction, the Initialize pattern |

### Strengthen -- Fortify your process

| Chapter | What you'll learn |
|---------|-------------------|
| [5. Working with Your Team](guide/05-working-with-your-team.md) | Source control patterns, code review, team collaboration |
| [6. Testing and Validation](guide/06-testing-and-validation.md) | Docker testing, CI validation, WhatIf as safety net |
| [7. CI/CD Integration](guide/07-cicd-integration.md) | Pipeline examples, env var config, secrets, build/deploy model |
| [8. Rollback and Recovery](guide/08-rollback-and-recovery.md) | What rolls back automatically, procedures, best practices |

### Succeed -- Deploy with confidence

| Chapter | What you'll learn |
|---------|-------------------|
| [9. Power Workflows](guide/09-power-workflows.md) | Script tokens, multi-database products, DataTongs, execution slots |
| [10. Multi-Tenant Deployments](guide/10-multi-tenant-deployments.md) | Schema-per-tenant fan-out, tenant onboarding, cross-schema references |
| [11. Edge Cases & Escape Hatches](guide/11-edge-cases.md) | Migration scripts, renames, cross-dependencies, special types |

### Appendix

| Chapter | What you'll learn |
|---------|-------------------|
| [12. Troubleshooting](guide/12-troubleshooting.md) | Common issues, log reading, diagnostics |

---

## Reference

Already know what you're looking for? Jump straight to the details.

| Reference | Covers |
|-----------|--------|
| [SchemaTongs](reference/schematongs.md) | Schema extraction across SQL Server, PostgreSQL, MySQL, and MariaDB — object types, config, orphan detection, validation |
| [SchemaQuench](reference/schemaquench.md) | Deployment engine — execution flow, slots, WhatIf, migration tracking |
| [Recyclebin](reference/recyclebin.md) | Soft-drop and restore hooks for recoverable table removal across SQL Server, PostgreSQL, MySQL, and MariaDB |
| [DataTongs](reference/datatongs.md) | Data extraction — MERGE generation, type handling, key detection |
| [SchemaShears](reference/schemashears.md) | Patch builder — carve an object-level patch package from a full product via a manifest |
| [Configuration](reference/configuration.md) | Shared CLI switches, config hierarchy, environment variables, logging |
| [Schema Packages](reference/schema-packages.md) | Product/Template JSON, folder structure, table definition format |
| [Custom Properties](reference/custom-properties.md) | Attach your own metadata via the `Extensions` carrier, driving tokens and governance |
| [Script Tokens](reference/script-tokens.md) | Token syntax, resolution order, overrides, environment variables |
| [Error Codes & Reporting](reference/error-codes-and-reporting.md) | Per-platform error codes, the three reporting channels, where each engine logs faults, `VerboseLogging` |

---

## Demo Products

Nothing beats working with real data. The repository includes demo products you can extract, deploy, and explore against a local database container.

- **AdventureWorks** — real-world complexity
- **Chinook** — the classic media-store schema
- **Northwind** — the canonical getting-started schema
- **Sakila** — the DVD rental store schema

All four demos are deployed on all four platforms (SQL Server, PostgreSQL, MySQL, and MariaDB) by the per-platform `docker-compose.yml` files under `Demos/`. Each one was extracted and deployed end-to-end with SchemaSmith tools.
