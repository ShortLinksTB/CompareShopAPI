#!/usr/bin/env bash
set -euo pipefail

image="compareshop-api-workers:runtime-check"
docker build --file Dockerfile.workers --tag "$image" .

runtimes="$(docker run --rm --entrypoint dotnet "$image" --list-runtimes)"
printf '%s\n' "$runtimes"
if ! grep -Eq '^Microsoft\.AspNetCore\.App 10\.' <<< "$runtimes"; then
  echo "Worker image is missing Microsoft.AspNetCore.App 10.x" >&2
  exit 1
fi

uid="$(docker run --rm --entrypoint id "$image" -u)"
if [[ "$uid" == "0" ]]; then
  echo "Worker image must run as a non-root user" >&2
  exit 1
fi

startup_log="$(mktemp)"
trap 'rm -f "$startup_log"' EXIT
set +e
timeout --foreground 3s docker run --rm "$image" >"$startup_log" 2>&1
startup_status=$?
set -e
cat "$startup_log"
if [[ "$startup_status" -ne 124 ]]; then
  echo "Worker did not remain running during the startup smoke check (exit $startup_status)" >&2
  exit 1
fi
if grep -q "No frameworks were found" "$startup_log"; then
  echo "Worker failed to load a required .NET framework" >&2
  exit 1
fi
