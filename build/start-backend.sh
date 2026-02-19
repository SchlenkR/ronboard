#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
echo "Starting Ronboard Backend on http://localhost:5000 ..."
dotnet run --project backend/Ronboard.Api
