#!/usr/bin/env bash
# Saves the database containers' logs to db-logs.txt and writes every deadlock they record into the job summary:
# PostgreSQL's "deadlock detected" entries with their processes and statements, and SQL Server's graphs, which
# trace flag 1222 (docker-compose.yml) writes to its log. Runs whether the tests passed or not, since a deadlock the
# engine absorbs by retrying fails no test of its own.
set -u
docker compose logs --no-color > db-logs.txt
pg=$(grep -c 'deadlock detected' db-logs.txt || true)
ms=$(grep -c 'deadlock-list' db-logs.txt || true)
{
  echo "### Deadlocks in the database logs"
  echo
  echo "PostgreSQL: ${pg:-0}. SQL Server: ${ms:-0}."
  if [ "${pg:-0}" -gt 0 ]; then
    echo
    echo '```'
    grep -A 12 'deadlock detected' db-logs.txt | head -400
    echo '```'
  fi
  if [ "${ms:-0}" -gt 0 ]; then
    echo
    echo '```'
    grep -A 60 'deadlock-list' db-logs.txt | head -600
    echo '```'
  fi
} >> "${GITHUB_STEP_SUMMARY:-/dev/stdout}"
