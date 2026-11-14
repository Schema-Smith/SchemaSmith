---
name: install
description: Install the SchemaSmith command-line tools (SchemaQuench, SchemaTongs) on this machine and confirm they run. Use when the user wants to install, upgrade or check SchemaSmith.
---

# Install the SchemaSmith tools

1. Find the operating system from the environment (and the Linux distribution from `/etc/os-release`).
2. Check whether the tools are already installed: run `SchemaQuench --version` and `SchemaTongs --version`. If both answer, say which version is installed and stop unless the user wants another one.
3. Show the install command for this machine and run it only when the user agrees:

| Machine | Command |
|---|---|
| Windows, winget | `winget install SchemaSmith.SchemaSmith` |
| Windows, Chocolatey | `choco install schemasmith` |
| Linux or macOS | `curl -fsSL https://schemasmith.com/dl/install.sh \| sh` |
| Arch Linux | `yay -S schemasmith-bin` |
| Debian, Ubuntu, RHEL, Fedora | the `.deb` or `.rpm` from the latest release: https://github.com/Schema-Smith/SchemaSmith/releases |
| Docker (SchemaQuench only) | `docker pull schemasmithyfree/schemaquench:latest` |

   `install.sh` takes `INSTALL_VERSION` and `INSTALL_DIR` to choose a version and folder. A package manager may need an elevated shell; if the command fails for that reason, say so and let the user run it.
4. Open a new shell if the install changed `PATH`, then confirm with `SchemaQuench --version` and `SchemaTongs --version`. Done when both answer.

The full instructions, including manual downloads and checksums: https://github.com/Schema-Smith/SchemaSmith/blob/main/docs/end-user/guide/installation.md
