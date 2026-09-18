#Requires -Version 5.1
# Render the winget manifest templates for a published SchemaSmith release and submit them to
# microsoft/winget-pkgs. SHAs come from the release's authoritative SHA256SUMS asset.
#   -ValidateOnly : render + `winget validate` only (local check; no gh token / no submission).
# Runs on windows-latest in distribution-publish.yml; needs `gh`, `winget`, and (to submit)
# `wingetcreate` on PATH plus a WINGET_PAT.
[CmdletBinding()]
param(
    [string]$Version = $env:SS_VERSION,
    [string]$Token   = $env:WINGET_PAT,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
if (-not $Version) { throw 'Version not provided (pass -Version or set SS_VERSION).' }
$repo = 'Schema-Smith/SchemaSmith'
$tag  = "v$Version"

$work = Join-Path ([System.IO.Path]::GetTempPath()) "ss-winget-$Version"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null

# Authoritative SHA-256s from the release's SHA256SUMS asset (matches exactly what shipped).
gh release download $tag --repo $repo --pattern 'SHA256SUMS' --dir $work
if ($LASTEXITCODE -ne 0) { throw "gh release download failed for $tag" }
$sums = Get-Content (Join-Path $work 'SHA256SUMS')
function Get-Sha([string]$name) {
    foreach ($line in $sums) {
        $parts = $line -split '\s+'
        if ($parts.Count -ge 2 -and $parts[-1].TrimStart('*') -eq $name) { return $parts[0].ToUpper() }
    }
    throw "SHA256 for $name not found in SHA256SUMS"
}
$shaX64   = Get-Sha "SchemaSmith-$Version-win-x64.zip"
$shaArm64 = Get-Sha "SchemaSmith-$Version-win-arm64.zip"

# Render manifest templates (UTF-8, no BOM — winget-pkgs convention).
$here = Split-Path -Parent $PSCommandPath
$out  = Join-Path $work 'manifests'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
Get-ChildItem $here -Filter 'SchemaSmith.SchemaSmith*.yaml' | ForEach-Object {
    $text = ([System.IO.File]::ReadAllText($_.FullName)).
        Replace('__VERSION__',   $Version).
        Replace('__SHA_X64__',   $shaX64).
        Replace('__SHA_ARM64__', $shaArm64)
    [System.IO.File]::WriteAllText((Join-Path $out $_.Name), $text, $utf8NoBom)
}
Write-Host "Rendered manifests -> $out"

# Schema-validate locally when winget is present (wingetcreate also validates before it submits).
if (Get-Command winget -ErrorAction SilentlyContinue) {
    winget validate --manifest $out
    if ($LASTEXITCODE -ne 0) { throw 'winget validate failed.' }
} else {
    Write-Host 'winget not available — skipping local validate (wingetcreate validates on submit).'
}

if ($ValidateOnly) { Write-Host 'ValidateOnly: rendered + validated, no submission.'; return }
if (-not $Token)   { Write-Host 'No WINGET_PAT set — skipping winget-pkgs submission.'; return }

# Hand the PAT over in the environment, not on the command line. wingetcreate warns about
# --token on every run ("Using the --token argument may result in the token being logged",
# https://aka.ms/winget-create-token) and WINGET_CREATE_GITHUB_TOKEN is the form it documents.
# Nothing leaked -- the argument is not echoed and the job log carried only the warning -- so this
# is hardening: an argument is visible in process listings and is one verbose-logging change away
# from the log. Scoped to this process, so it does not outlive the submit.
$env:WINGET_CREATE_GITHUB_TOKEN = $Token
try {
    wingetcreate submit $out
    if ($LASTEXITCODE -ne 0) { throw 'wingetcreate submit failed.' }
}
finally {
    Remove-Item Env:\WINGET_CREATE_GITHUB_TOKEN -ErrorAction SilentlyContinue
}
Write-Host 'Submitted to winget-pkgs.'
