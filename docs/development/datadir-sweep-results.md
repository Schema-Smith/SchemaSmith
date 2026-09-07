# DataDirectory sweep results

Appended by `scripts/run-datadir-sweep.sh`. CI cannot run this sweep -- the demo/CI containers
have no /ddspace directory and MySQL needs it listed in innodb_directories -- so this file is the
standing evidence that it ran, and against what.

## 2026-09-04T19:25:59Z - commit fc8fef01

| Target | Result | Infra |
|---|---|---|
| mariadb @ 13418 | NEVER READY | n/a |
| mysql @ 13480 | Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 6 s - Schema.IntegrationTests.dll (net10.0) | smoke check passed |

## 2026-09-04T19:32:12Z - commit fc8fef01

| Target | Result | Infra |
|---|---|---|
| mariadb @ 13418 | Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 3 s - Schema.IntegrationTests.dll (net10.0) | smoke check passed |
| mysql @ 13480 | Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 9 s - Schema.IntegrationTests.dll (net10.0) | smoke check passed |
