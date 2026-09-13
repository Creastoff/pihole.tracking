#!/usr/bin/env bash
set -Eeuo pipefail

if ! command -v docker >/dev/null 2>&1; then
    echo 'Docker is not installed or is not on the SSH user PATH.' >&2
    exit 20
fi

if docker compose version >/dev/null 2>&1; then
    docker compose up -d --build
elif command -v docker-compose >/dev/null 2>&1; then
    docker-compose up -d --build
else
    echo 'Docker Compose is not installed.' >&2
    exit 21
fi
