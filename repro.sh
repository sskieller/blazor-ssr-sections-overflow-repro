#!/usr/bin/env bash
# Repro driver for https://github.com/dotnet/aspnetcore/issues/69035
#
# The bug is a process-killing stack overflow inside EndpointHtmlRenderer's synchronous
# static-SSR HTML walk, so it can never surface as a failed assertion: the runtime aborts
# with SIGABRT and the process exits 134. This script therefore looks at the EXIT CODE of
# each round, not at the test results.
#
#   REPRO_ROUNDS       how many times to run the suite          (default 5)
#   REPRO_ITERATIONS   requests per route per round             (default 300)
#   REPRO_PARALLELISM  concurrent in-flight requests            (default 8)
#   REPRO_NO_TASKSET   set to 1 to skip pinning to two cores
#
# Dumps land in /tmp/repro-<pid>.dmp (heap included) so the cycling component ids and the
# disposed SectionOutletContentRenderer are inspectable with dotnet-dump.
set -uo pipefail

ROUNDS="${REPRO_ROUNDS:-5}"
ITERATIONS="${REPRO_ITERATIONS:-300}"
PARALLELISM="${REPRO_PARALLELISM:-8}"
CONFIGURATION="${REPRO_CONFIGURATION:-Release}"

cd "$(dirname "$0")"

export REPRO_ITERATIONS="$ITERATIONS"
export REPRO_PARALLELISM="$PARALLELISM"

# The crash trap. Type 2 = "heap included": the render tree the analysis needs lives there.
export DOTNET_DbgEnableMiniDump=1
export DOTNET_DbgMiniDumpType=2
export DOTNET_DbgMiniDumpName=/tmp/repro-%p.dmp

# The bug has only ever been seen on 2-core Linux CI runners, never on a many-core dev box.
# Pin to two cores when taskset is available so a fat local machine behaves like the runner.
PIN=()
if [[ "${REPRO_NO_TASKSET:-0}" != "1" ]] && command -v taskset >/dev/null 2>&1; then
  PIN=(taskset -c 0,1)
  echo "== pinning to cores 0,1 with taskset"
else
  echo "== taskset unavailable or disabled; running unpinned (the bug is far less likely)"
fi

echo "== rounds=$ROUNDS iterations=$ITERATIONS parallelism=$PARALLELISM configuration=$CONFIGURATION"

dotnet build -c "$CONFIGURATION" || exit $?

reproduced=0
for ((round = 1; round <= ROUNDS; round++)); do
  echo "== round $round/$ROUNDS"
  "${PIN[@]}" dotnet run --project tests/ReproApp.Tests -c "$CONFIGURATION" --no-build
  rc=$?
  echo "== round $round exit code: $rc"

  if [[ "$rc" -eq 134 ]]; then
    echo "REPRODUCED (exit 134)"
    reproduced=1
    break
  fi

  if [[ "$rc" -ne 0 ]]; then
    echo "== round $round failed with exit $rc — not the crash we are hunting, stopping"
    break
  fi
done

echo "== dumps:"
shopt -s nullglob
dumps=(/tmp/repro-*.dmp)
if ((${#dumps[@]})); then
  ls -la "${dumps[@]}"
else
  echo "   (none)"
fi

if ((reproduced)); then
  exit 134
fi

echo "NOT REPRODUCED in $ROUNDS round(s)"
exit 0
