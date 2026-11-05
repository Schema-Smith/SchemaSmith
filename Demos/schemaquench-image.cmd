@echo off
:: Called by every run-demo.cmd as: call schemaquench-image.cmd "<repo root>" [--from-source]
:: No setlocal here, so SCHEMAQUENCH_IMAGE reaches the caller. Picks the image the quench services run:
::   default        the published release image (ghcr.io/schema-smith/schemaquench:2), pulled fresh
::   --from-source  publish SchemaQuench from this checkout and build a local image (needs the .NET SDK)
:: Set SCHEMAQUENCH_IMAGE yourself to pin a release, e.g. ghcr.io/schema-smith/schemaquench:2.8.0.

if /i "%~2"=="--from-source" goto source
if not defined SCHEMAQUENCH_IMAGE set "SCHEMAQUENCH_IMAGE=ghcr.io/schema-smith/schemaquench:2"
echo Pulling %SCHEMAQUENCH_IMAGE%...
:: A pull failure is fine when the image is already local (offline, or a locally built tag).
docker pull -q %SCHEMAQUENCH_IMAGE% >nul 2>&1 || docker image inspect %SCHEMAQUENCH_IMAGE% >nul || exit /b 1
exit /b 0

:source
echo Publishing SchemaQuench from source...
call "%~1\build-schemaquench.cmd" || exit /b 1
docker build -q -t schemaquench:source -f "%~1\SchemaQuench\Dockerfile.release" "%~1\SchemaQuench" >nul || exit /b 1
set "SCHEMAQUENCH_IMAGE=schemaquench:source"
exit /b 0
