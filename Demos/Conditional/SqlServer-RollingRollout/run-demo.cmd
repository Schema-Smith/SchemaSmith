@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%..\..\.."
set "REPO_ROOT=%CD%"
popd

call "%REPO_ROOT%\Demos\schemaquench-image.cmd" "%REPO_ROOT%" %1
if errorlevel 1 exit /b %errorlevel%

echo Starting SQL Server conditional-deployment demo...
cd /d "%SCRIPT_DIR%"

docker compose up --build --abort-on-container-exit verify
