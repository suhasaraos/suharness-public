#!/usr/bin/env bash
# Build and start the SuHarness browser UI.
#
# Runs from wherever the repository happens to be checked out.
#
# Launching through Suharness.Cli rather than Suharness.Web is deliberate --
# the CLI is what enforces the loopback-only binding policy. See SECURITY.md.

set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

port="${1:-3090}"

echo "Building SuHarness..."
dotnet build Suharness.slnx --nologo -v q

echo "Starting SuHarness on http://127.0.0.1:${port} ..."
exec dotnet run --project src/Suharness.Cli/Suharness.Cli.csproj --no-build -- web --port "${port}"