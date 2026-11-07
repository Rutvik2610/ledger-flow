#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
if [ -e .env ]; then echo '.env already exists; preserving it.'; exit 0; fi
umask 077
password="$(openssl rand -hex 24)Aa1!"
printf 'SQL_PASSWORD=%s\n' "$password" > .env
echo 'Created ignored .env with a random local SQL password.'
