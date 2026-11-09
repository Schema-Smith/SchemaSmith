---
name: ci
description: Write a GitHub Actions workflow that uses the SchemaSmith Deploy action to validate and preview on pull requests, deploy on merge, and check drift on a schedule. Use when the user wants SchemaSmith in CI.
---

# Set up the GitHub Action

1. Ask for the package folder in the repository, the target server and user, and which branch deploys. Never ask for the password: it is a repository or environment secret the user creates.
2. Find the latest release tag at https://github.com/Schema-Smith/SchemaSmith/releases and pin the action to it: `Schema-Smith/schemasmith-deploy@<tag>`. The tag also chooses the SchemaQuench version the action runs.
3. Write `.github/workflows/schemasmith.yml` with three jobs:
   - **pull requests:** `mode: validate`, then `mode: whatif`, with `pr-comment: true` so the preview is posted on the pull request. The job needs `permissions: pull-requests: write`.
   - **pushes to the deploy branch:** `mode: deploy`, in a GitHub environment the user can protect with required reviewers.
   - **a schedule:** `mode: drift`, with `continue-on-error: true` if drift should be reported without failing the run.
   Each job uses `actions/checkout` and passes `product-path`, `server`, `user` and `password: ${{ secrets.<NAME> }}`. Other settings go through `extra-args` as `--Key:value`.
4. Name the secrets the user must create, and where (repository or environment), and the GitHub environment if one is used.
5. Say what the action reports: the summary on the job page, each failed target as an annotation (on the failing script when it is in the repository), and the outputs `exit-code`, `log-dir`, `summary-path` and `summary-json-path`.

Done when the workflow file exists and the user knows which secrets to add. The action's reference: https://github.com/Schema-Smith/SchemaSmith/blob/main/docs/end-user/guide/07-cicd-integration.md
