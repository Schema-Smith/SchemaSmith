# Encryption test infrastructure (LOCAL-ONLY)

At-rest table encryption (`ENCRYPTED=YES` on MariaDB, `ENCRYPTION='Y'` on MySQL) needs a server-side
key-management backend that the stock demo/CI containers do not have, and GitHub Actions `services:`
cannot mount the required plugin config. So encryption integration tests run locally via
`scripts/run-encryption-sweep.sh`, the same pattern as the genuine-binary sweep, not in CI.

- **`mariadb/`** — WORKING. `file_key_management` plugin + a fixed TEST key. `run-encryption-sweep.sh`
  builds this image and runs the `Encryption` MariaDB tests against it.
- **`mysql/`** — BLOCKED. `component_keyring_file` will not initialize inside the Oracle `mysql:8.0`
  entrypoint (Component_status stays `Disabled`; MySQL bug #108197 family). MySQL encryption integration
  tests are `[Explicit]`/skipped with a documented reason until a custom-entrypoint workaround (init the
  datadir without the keyring, then start with it) or a pre-baked keyring is added here. The encryption
  emit/parse/converge CODE is engine-symmetric and covered by the MariaDB tests + review meanwhile.

The keys here are TEST-ONLY fixtures (like the TestUser password), not secrets.
