# Installation

Most database tooling ships with a runtime to install, a service to configure, and a license server to point at. SchemaSmith ships as four self-contained executables. Pick the channel that matches your environment and one command later you have `schemaquench`, `schematongs`, `datatongs`, and `schemashears` on your PATH -- Windows, Linux, or macOS, no .NET runtime, no system dependencies, no fuss.

## Pick Your Path

| Platform | Recommended | Alternatives |
|----------|-------------|--------------|
| Windows | `choco install schemasmith` | Manual ZIP download |
| Debian / Ubuntu | `.deb` package | `install.sh`, manual `.tar.gz` |
| RHEL / Fedora / Amazon Linux | `.rpm` package | `install.sh`, manual `.tar.gz` |
| Other Linux | `install.sh` | Manual `.tar.gz` |
| Arch Linux | AUR (`schemasmith-bin`) | `install.sh`, manual `.tar.gz` |
| Anywhere with Docker | `schemasmithyfree/schemaquench` image (SchemaQuench only) | -- |
| macOS | `install.sh` | Manual `.tar.gz` |

Every channel except the Docker image installs the same four CLI commands and the same self-contained binaries -- the image carries SchemaQuench alone. The choice is logistics -- what fits your environment's policies, your update workflow, your CI runners. Pick once and the channel handles installs and upgrades from there.

## Windows: Chocolatey

```powershell
choco install schemasmith
```

One command, four CLIs, signed binaries embedded. No .NET runtime, no checksum files to maintain. The package on the [Chocolatey community feed](https://community.chocolatey.org/packages/schemasmith) is named `schemasmith` and includes `schemaquench.exe`, `schematongs.exe`, `datatongs.exe`, and `schemashears.exe`. Upgrades flow through `choco upgrade schemasmith` like any other Chocolatey package.

Every release republishes automatically when GitHub publishes the corresponding tag, so the version on Chocolatey tracks the version on the GitHub release page within minutes.

## Debian and Ubuntu: .deb

Download the `.deb` for your architecture and install with `dpkg`. Replace `v2.0.0` and the version in the filename with the release you want -- the [latest release page](https://github.com/Schema-Smith/SchemaSmith/releases/latest) lists what's available.

```bash
wget https://github.com/Schema-Smith/SchemaSmith/releases/download/v2.0.0/schemasmith_2.0.0_amd64.deb
sudo dpkg -i schemasmith_2.0.0_amd64.deb
```

Binaries land under `/usr/lib/schemasmith/` with symlinks in `/usr/bin/` so `schemaquench`, `schematongs`, `datatongs`, and `schemashears` are immediately on PATH. Zero declared package dependencies -- the binaries are fully self-contained and bundle their own ICU runtime alongside the executables, so they work on minimal containers and hardened distros that ship without `libicu`.

Both `amd64` and `arm64` builds are published with every release. Use `schemasmith_<version>_arm64.deb` on ARM hosts.

## RHEL, Fedora, and Amazon Linux: .rpm

Same idea on RPM-based distros:

```bash
wget https://github.com/Schema-Smith/SchemaSmith/releases/download/v2.0.0/schemasmith-2.0.0.x86_64.rpm
sudo rpm -i schemasmith-2.0.0.x86_64.rpm
```

Layout matches the `.deb` package -- binaries under `/usr/lib/schemasmith/`, symlinks in `/usr/bin/`, no system dependencies. Both `x86_64` and `aarch64` builds are published per release. Works on Fedora, RHEL/CentOS Stream, Amazon Linux 2023, and any other distro that supports `rpm -i`.

## Arch Linux: AUR

```bash
yay -S schemasmith-bin
```

The `schemasmith-bin` package installs all four CLI commands from the official
release binaries -- any AUR helper works, or build it directly with `makepkg`.
The PKGBUILD is updated with each release.

## Docker: run SchemaQuench without installing

```bash
docker pull schemasmithyfree/schemaquench:latest
```

The SchemaQuench container image is published multi-arch (`linux/amd64` +
`linux/arm64`) to Docker Hub (`schemasmithyfree/schemaquench`) and GHCR
(`ghcr.io/schema-smith/schemaquench`) with each release. Tags: `latest`,
immutable `X.Y.Z`, and floating `X.Y` and `X`. This channel ships SchemaQuench
only, for running deployments where nothing may be installed. Configure
through `SmithySettings_` environment variables or a mounted
`SchemaQuench.settings.json`.

## Linux and macOS: install.sh

The universal Unix path. One command, any glibc-based Linux distro, any modern macOS, both `x64` and `arm64`:

```sh
curl -fsSL https://schemasmith.com/dl/install.sh | sh
```

`install.sh` detects your OS and architecture, resolves the latest release without a GitHub API token, downloads the matching `.tar.gz` bundle, verifies SHA-256 against the release manifest, and installs the four CLI commands onto your PATH. The script is POSIX `sh` -- no bash-isms -- so it runs on whatever shell your container or distro happens to ship.

By default it installs to `/usr/local/bin` if you run it as root and `~/.local/bin` otherwise. Two environment variables let you override:

| Variable | Default | Purpose |
|----------|---------|---------|
| `INSTALL_VERSION` | latest release | Pin to a specific version (e.g., `2.0.0`) |
| `INSTALL_DIR` | as above | Custom install path |

Pin a version and target a custom directory in one go:

```sh
INSTALL_VERSION=2.0.0 INSTALL_DIR=$HOME/tools sh -c "$(curl -fsSL https://schemasmith.com/dl/install.sh)"
```

When the install directory is not on your `$PATH`, the script prints shell-specific PATH-fixup instructions before exiting -- `export` lines for bash and zsh, `fish_add_path` for fish -- so you can paste once and move on.

## Manual Download

When policy or network constraints rule out the curl-pipe path, download the bundle directly. Linux and macOS bundles are `.tar.gz`; Windows bundles are `.zip`.

```bash
gh release download v2.0.0 --repo Schema-Smith/SchemaSmith --pattern 'SchemaSmith-2.0.0-linux-x64.tar.gz'
tar -xzf SchemaSmith-2.0.0-linux-x64.tar.gz
for tool in SchemaQuench SchemaTongs DataTongs SchemaShears; do
    lower=$(echo "$tool" | tr '[:upper:]' '[:lower:]')
    sudo install -m 0755 "$tool" "/usr/local/bin/$lower"
done
```

The rename to lowercase matches the `schemaquench` / `schematongs` / `datatongs` / `schemashears` command names the other channels install. PascalCase invocation also works on case-sensitive filesystems if you'd rather keep the binaries' original names.

On Windows:

```powershell
gh release download v2.0.0 --repo Schema-Smith/SchemaSmith --pattern 'SchemaSmith-2.0.0-win-x64.zip'
Expand-Archive SchemaSmith-2.0.0-win-x64.zip -DestinationPath C:\Tools\SchemaSmith
# Add C:\Tools\SchemaSmith to PATH via System Properties or:
[Environment]::SetEnvironmentVariable('Path', $env:Path + ';C:\Tools\SchemaSmith', 'User')
```

The available RIDs are `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`, and `win-arm64`. Pick the one that matches your target.

> **Note:** `install.sh` does the same job with one fewer step on Linux and macOS. Reach for manual download when policy or network constraints rule out the curl-pipe pattern, when you're staging the binaries into a custom layout (CI artifact caches, container images, air-gapped mirrors), or when you want to verify checksums by hand before extracting.

## Verifying Your Install

A few checks anyone can run, from a basic hash comparison available on any release up to build provenance and SBOM checks introduced in v2.4.0.

### SHA-256 verification

Every release publishes a single `SHA256SUMS` file alongside the artifacts. The `.deb`, `.rpm`, manual `.tar.gz`, and Windows `.zip` paths are exactly where this matters -- `install.sh` runs the same check automatically.

```bash
gh release download v2.0.0 --repo Schema-Smith/SchemaSmith \
    --pattern 'SHA256SUMS' --pattern '*.tar.gz' --pattern '*.deb' --pattern '*.rpm'
sha256sum -c SHA256SUMS                    # Linux
shasum -a 256 -c SHA256SUMS                # macOS
```

The `-c` flag fails loudly if any line in the manifest doesn't match the local file. A mismatch is a security incident -- re-download from a clean network or open an issue.

### Authenticode signing

Every `.exe` in a Windows release is signed via Azure Trusted Signing. Chocolatey installs already-signed binaries, but the manual ZIP path is where this check earns its keep:

```cmd
signtool verify /pa /v SchemaQuench.exe
signtool verify /pa /v SchemaTongs.exe
signtool verify /pa /v DataTongs.exe
```

`signtool` ships with the Windows SDK. A successful verify confirms the signature chains to a Microsoft-trusted root and the binary hasn't been altered since signing.

### Build provenance attestation

Starting with the v2.4.0 release, every release artifact also carries a signed build-provenance attestation, generated as part of the official release workflow. Verify one with the GitHub CLI:

```bash
gh attestation verify SchemaSmith-2.4.0-linux-x64.tar.gz --repo Schema-Smith/SchemaSmith
```

A successful verification confirms the artifact was built by `.github/workflows/release.yml` in the `Schema-Smith/SchemaSmith` repository -- not just that the file matches a hash, but where it came from. That's a step beyond SHA-256: a checksum proves the file matches a published value, an attestation proves who published it.

### CycloneDX SBOM

Starting with the v2.4.0 release, each release also publishes a CycloneDX-format software bill of materials as a release asset named `SchemaSmith-<version>.cdx.json`, listing the components that went into that build.

```bash
gh release download v2.4.0 --repo Schema-Smith/SchemaSmith --pattern 'SchemaSmith-2.4.0.cdx.json'
```

Useful for procurement review and dependency auditing.

## Per-Platform Notes

> **Windows:** SmartScreen warnings are eliminated by Authenticode signing on every release. If you encounter "Windows protected your PC" on a pre-release or unsigned development build, click "More info" then "Run anyway." Signed v2.0.0+ releases will not trigger this prompt.

> **Linux PATH:** `install.sh` prints shell-specific PATH-fixup instructions when `INSTALL_DIR` is not on `PATH` -- bash/zsh `export` lines, fish `fish_add_path`. The `.deb` and `.rpm` packages install symlinks in `/usr/bin/` so `PATH` works automatically. Manual `.tar.gz` extraction is where `PATH` guidance matters most: append `export PATH="$PATH:/path/to/install/dir"` to your shell rc, then `source` it or open a new terminal.

> **macOS Gatekeeper:** `curl`-downloaded files don't carry the `com.apple.quarantine` xattr, so Gatekeeper typically does not interfere with `install.sh` or manually-downloaded `.tar.gz` archives. If you see "cannot be opened because the developer cannot be verified," strip the quarantine attribute: `xattr -d com.apple.quarantine /path/to/binary`. Apple Developer ID signing and notarization are on the post-v2 roadmap; once they land this workaround disappears.

## What's Next

Tools in place? Time to put them to work. Run any of them with no arguments before you've configured it and it tells you which settings file it read and which settings to fill in.

**[Quick Start →](02-quick-start.md)**
