# Genuine-binary sweep results

Appended by `scripts/run-genuine-sweep.sh`. CI cannot run this sweep -- no pre-2017 SQL
Server Linux image exists -- so this file is the standing evidence that it ran, and against what.

## 2026-08-26T17:26:17Z - commit 435c1d66

| Target | Result | Expected |
|---|---|---|
| SQL Server @ 14330 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 10 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14331 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 25 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14332 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 26 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14333 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 7 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |

## 2026-08-27T20:29:55Z - commit 50605d09

| Target | Result | Expected |
|---|---|---|
| SQL Server @ 14330 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 19 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14331 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 29 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14332 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 27 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14333 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 8 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |

## 2026-08-27T20:34:32Z - modern bands - commit 80b0d96d

| Target | Result | Expected |
|---|---|---|
| mcr.microsoft.com/mssql/server:2017-latest @ 14340 | NEVER READY | full SqlServer category |
| mcr.microsoft.com/mssql/server:2022-latest @ 14342 | NEVER READY | full SqlServer category |
| mcr.microsoft.com/mssql/server:2025-latest @ 14345 | NEVER READY | full SqlServer category |

> **The NEVER READY rows above are a tooling failure, not a product finding.** The readiness probe
> passed `$SQLCMD` to `docker exec` as a bare argv entry, and Git Bash rewrote it to
> `C:/Program Files/Git/opt/...` before docker saw it, so no band could ever report ready. Fixed in
> `scripts/run-modern-band-sweep.sh` (every container-side path now goes inside an `sh -c` string), and
> the script now exits non-zero instead of 0 when a band did not run. Real results follow below.

## 2026-08-27T21:14:36Z - commit aa26d3ad

| Target | Result | Expected |
|---|---|---|
| SQL Server @ 14330 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 10 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14331 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 22 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14332 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 24 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14333 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 6 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |

## 2026-08-27T21:17:52Z - modern bands - commit 527f5213

| Target | Result | Expected |
|---|---|---|
| mcr.microsoft.com/mssql/server:2017-latest (14.0.3540.1) | **Failed!** - 508 failed, 98 passed across 4 assemblies | full SqlServer category, 0 failed |
| mcr.microsoft.com/mssql/server:2022-latest (16.0.4260.1) | **Failed!** - 508 failed, 98 passed across 4 assemblies | full SqlServer category, 0 failed |
| mcr.microsoft.com/mssql/server:2025-latest (17.0.4075.5) | **Failed!** - 508 failed, 98 passed across 4 assemblies | full SqlServer category, 0 failed |


> **These three rows originally read `Passed!`. They were wrong, and the bug was in the sweep.**
> The row recorded only the FIRST assembly to finish -- `Schema.IntegrationTests`, 50/50 green --
> while `SchemaQuench.IntegrationTests` failed 464/464 and `SchemaTongs.IntegrationTests` 44/59 in
> the same band. Corrected above to the real aggregate; the script now sums every assembly and
> refuses to exit 0 on a `Failed!` row.
>
> **The 508 failures are NOT a product finding.** Every one is `OneTimeSetUp` failing with
> *"Full-Text Search is not installed"* (error 7609): these bands are stock
> `mcr.microsoft.com/mssql/server` containers, and nothing installs the full-text component the
> SQL Server fixtures require. CI installs `mssql-server-fts` explicitly; the local demo server
> builds it in. **The modern bands remain UNCERTIFIED for this commit** -- see the header of
> `scripts/run-modern-band-sweep.sh` for what has to change before they mean anything.
## 2026-08-27T22:11:47Z - commit 019ff5ae

| Target | Result | Expected |
|---|---|---|
| SQL Server @ 14330 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 19 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14331 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 36 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14332 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 28 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14333 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 9 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |

## 2026-08-30T08:33:10Z - modern bands - commit 8c062adf

| Target | Result | Expected |
|---|---|---|
| mcr.microsoft.com/mssql/server:2017-latest @ 14340 | SEMANTIC DB PROVISION FAILED | full SqlServer category |
| mcr.microsoft.com/mssql/server:2022-latest (16.0.4265.3) @ 14342 | Passed! - 0 failed, 606 passed across 4 assemblies | full SqlServer category, 0 failed |
| mcr.microsoft.com/mssql/server:2025-latest (17.0.4075.5) @ 14345 | Passed! - 0 failed, 606 passed across 4 assemblies | full SqlServer category, 0 failed |

## 2026-08-30T09:50:06Z - modern bands - commit f7b554fd

| Target | Result | Expected |
|---|---|---|
| mcr.microsoft.com/mssql/server:2017-latest (14.0.3540.1) @ 14340 | Passed! - 0 failed, 605 passed across 4 assemblies + SEMANTIC COVERAGE SKIPPED (no semanticsdb) | full SqlServer category, 0 failed |
| mcr.microsoft.com/mssql/server:2022-latest (16.0.4265.3) @ 14342 | Passed! - 0 failed, 606 passed across 4 assemblies | full SqlServer category, 0 failed |
| mcr.microsoft.com/mssql/server:2025-latest (17.0.4075.5) @ 14345 | Passed! - 0 failed, 606 passed across 4 assemblies | full SqlServer category, 0 failed |

## 2026-09-02T10:19:12Z - commit 801a9c48

| Target | Result | Expected |
|---|---|---|
| SQL Server @ 14330 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 49 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14331 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 1 m - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14332 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 27 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14333 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 31 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |

## 2026-09-02T13:53:25Z - commit 44ee7087

| Target | Result | Expected |
|---|---|---|
| SQL Server @ 14330 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 11 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14331 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 26 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14332 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 26 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14333 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 8 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |

## 2026-09-02T14:36:41Z - commit 36b7a6cc

| Target | Result | Expected |
|---|---|---|
| SQL Server @ 14330 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 11 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14331 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 23 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14332 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 26 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14333 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 8 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |

## 2026-09-03T19:37:14Z - commit 2f4f47df

| Target | Result | Expected |
|---|---|---|
| SQL Server @ 14330 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 31 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14331 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 29 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14332 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 31 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14333 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 11 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |

## 2026-09-04T04:15:06Z - commit ea033a2a

| Target | Result | Expected |
|---|---|---|
| SQL Server @ 14330 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 13 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14331 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 25 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14332 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 30 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14333 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 7 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |

## 2026-09-04T04:27:17Z - commit adc60090

| Target | Result | Expected |
|---|---|---|
| SQL Server @ 14330 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 11 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14331 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 29 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14332 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 26 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14333 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 9 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |

## 2026-09-06T00:19:39Z - commit 1c40a0d1

| Target | Result | Expected |
|---|---|---|
| SQL Server @ 14330 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 12 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14331 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 25 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14332 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 26 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14333 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 7 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |

## 2026-09-06T10:24:40Z - modern bands - commit 4629da33

| Target | Result | Expected |
|---|---|---|
| mcr.microsoft.com/mssql/server:2017-latest (14.0.3540.1) @ 14340 | Passed! - 0 failed, 683 passed across 4 assemblies + SEMANTIC COVERAGE SKIPPED (no semanticsdb) | full SqlServer category, 0 failed |
| mcr.microsoft.com/mssql/server:2022-latest (16.0.4265.3) @ 14342 | Passed! - 0 failed, 697 passed across 4 assemblies | full SqlServer category, 0 failed |
| mcr.microsoft.com/mssql/server:2025-latest (17.0.4075.5) @ 14345 | Passed! - 0 failed, 699 passed across 4 assemblies | full SqlServer category, 0 failed |

## 2026-09-06T20:44:09Z - commit 5ffd7ac3

| Target | Result | Expected |
|---|---|---|
| SQL Server @ 14330 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 15 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14331 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 1 m 39 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14332 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 30 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |
| SQL Server @ 14333 | Passed!  - Failed:     0, Passed:     4, Skipped:     4, Total:     8, Duration: 9 s - Schema.IntegrationTests.dll (net10.0) | 4 passed / 4 skipped |

## 2026-09-06T20:55:13Z - modern bands - commit 24aa9fdf

| Target | Result | Expected |
|---|---|---|
| mcr.microsoft.com/mssql/server:2017-latest (14.0.3540.1) @ 14340 | Passed! - 0 failed, 683 passed across 4 assemblies + SEMANTIC COVERAGE SKIPPED (no semanticsdb) | full SqlServer category, 0 failed |
| mcr.microsoft.com/mssql/server:2022-latest (16.0.4265.3) @ 14342 | Passed! - 0 failed, 697 passed across 4 assemblies | full SqlServer category, 0 failed |
| mcr.microsoft.com/mssql/server:2025-latest (17.0.4075.5) @ 14345 | Passed! - 0 failed, 699 passed across 4 assemblies | full SqlServer category, 0 failed |
