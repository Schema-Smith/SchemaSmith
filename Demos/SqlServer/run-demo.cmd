@echo off
setlocal

call "%~dp0..\schemaquench-image.cmd" "%~dp0..\.." %1
if %ERRORLEVEL% neq 0 exit /b 1

echo Starting SQL Server demo...
:: `up -d` blocks on the service_completed_successfully chain, so by the time it
:: returns every quench has finished. Don't follow up with `docker compose wait
:: completed` -- it races with the completed service's cleanup and Docker Desktop
:: emits "no containers for project" when the container is already gone.
docker compose up --build -d
