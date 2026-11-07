#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
./scripts/dev-init.sh
docker compose up -d --wait sql redis
set -a
. ./.env
set +a
export LEDGERFLOW_TEST_SQL="Server=localhost,1433;Database=LedgerFlowTests;User Id=sa;Password=$SQL_PASSWORD;Encrypt=True;TrustServerCertificate=True"
dotnet test --no-restore "$@"
