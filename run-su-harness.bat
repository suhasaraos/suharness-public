@echo off
REM Build and start the SuHarness browser UI.
REM
REM Runs from wherever the repository happens to be checked out: %~dp0 is this
REM script's own directory, so there is no absolute path to keep in sync.
REM
REM Launching through Suharness.Cli rather than Suharness.Web is deliberate --
REM the CLI is what enforces the loopback-only binding policy. See SECURITY.md.

setlocal
cd /d "%~dp0"

set "SUHARNESS_PORT=%~1"
if "%SUHARNESS_PORT%"=="" set "SUHARNESS_PORT=3090"

echo Building SuHarness...
dotnet build Suharness.slnx --nologo -v q
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

echo Starting SuHarness on http://127.0.0.1:%SUHARNESS_PORT% ...
start "" "http://127.0.0.1:%SUHARNESS_PORT%"
dotnet run --project "src\Suharness.Cli\Suharness.Cli.csproj" --no-build -- web --port %SUHARNESS_PORT%
endlocal