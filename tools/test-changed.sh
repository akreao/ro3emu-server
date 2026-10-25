#!/usr/bin/env bash
# Runs the tests a change touches instead of the whole suite (docs/guide/testing.md, "Running tests while you work").
# CI still runs every test on Linux and Windows; this is the quick check while you work and before you push.
#
# It looks at the files changed since the merge base with --base (committed, staged, unstaged and untracked):
#   - a test file: its test classes;
#   - a .cs file of the server, the bot, the load test or the economy probe, for each type it declares: the test class
#     named after the type (MailService -> MailTests, GameLoop -> GameLoopTests) and every test class whose file names
#     the type. When that is more than --max classes, only the classes named after the type run (--wide runs them all);
#   - a data file of the server (Reconstructed/*.json): the same, for the code that names the file;
#   - the patcher or the setup tool (and their tests): that test project;
#   - docs, Markdown and client-side projects: nothing;
#   - anything that can change every test (project files, props, the fixture, content/, a deleted file, changed types
#     no test names): the CI set, every test project without client data and without Category=Slow.
#
# usage: tools/test-changed.sh [--base REF] [--max N] [--wide] [--no-build] [--print] [--all] [-- dotnet test arguments]
#   --base REF   compare with REF (default: origin/main)
#   --max N      the most test classes that run without --wide (default: 30)
#   --wide       run every test class that names a changed type, however many
#   --no-build   don't build first (you just built)
#   --print      only print what would run
#   --all        run the CI set whatever changed
set -euo pipefail
self=$(readlink -f "$0")
cd "$(dirname "$self")/.."

base=origin/main; max=30; wide=0; build=1; print=0; all=0; extra=()
while [ $# -gt 0 ]; do
  case "$1" in
    --base) base="$2"; shift 2 ;;
    --max) max="$2"; shift 2 ;;
    --wide) wide=1; shift ;;
    --no-build) build=0; shift ;;
    --print) print=1; shift ;;
    --all) all=1; shift ;;
    --) shift; extra=("$@"); break ;;
    *) echo "unknown option $1 (see the top of $0)" >&2; exit 2 ;;
  esac
done

tests_dir=tests/Ro3Emu.Tests
broad_reason=""
declare -A owners=() users=() projects=() seen_types=()
pending_types=()

broad() { [ -n "$broad_reason" ] || broad_reason="$1"; }

# The type names a C# file declares.
declared_types() { grep -oP '\b(class|struct|record|interface|enum)\s+\K[A-Z]\w*' "$1" 2>/dev/null | sort -u || true; }
declared_classes() { grep -oP '\bclass\s+\K[A-Z]\w*' "$1" 2>/dev/null | sort -u || true; }
is_test_file() { grep -qE '\[(Fact|Theory|ClientDataFact|ClientDataTheory)\b' "$1"; }

queue_types_of() {
  local t found=0
  while read -r t; do
    [ -n "$t" ] || continue
    found=1
    [ -n "${seen_types[$t]:-}" ] && continue
    seen_types[$t]=1
    pending_types+=("$t")
  done < <(declared_types "$1")
  [ "$found" = 1 ] || broad "$1 declares no type"
}

add_test_file() {
  local c
  while read -r c; do [ -n "$c" ] && owners[$c]=1; done < <(declared_classes "$1")
}

# A data file of the server: the types of the code that names it.
queue_readers_of() {
  local name file found=0
  name=$(basename "$1")
  while read -r file; do
    [ -n "$file" ] && { found=1; queue_types_of "$file"; }
  done < <(grep -rlF --include='*.cs' -- "$name" src/Ro3Emu.Server 2>/dev/null | grep -v '/obj/' || true)
  [ "$found" = 1 ] || broad "no server code names $name"
}

classify() {
  local f="$1"
  case "$f" in
    *.md|docs/*|.github/*|.claude/*|.editorconfig|LICENSE|*.png|*.svg|*.jpg) ;; # the build checks .editorconfig
    "$tests_dir"/Fixtures/*|"$tests_dir"/Snapshots/*|content/*) broad "$f is data every test can read" ;;
    src/Ro3Emu.Patcher/*|tests/Ro3Emu.Patcher.Tests/*) projects[tests/Ro3Emu.Patcher.Tests]=1 ;;
    src/Ro3Emu.Setup/*|src/Ro3Emu.DataExport/*|src/Ro3Emu.Desktop/*|tests/Ro3Emu.Setup.Tests/*)
      projects[tests/Ro3Emu.Setup.Tests]=1 ;;
    Directory.*|global.json|*.slnx|src/Ro3Emu.Server.Generators/*|src/Ro3Emu.Server/*.csproj|src/Ro3Emu.Bot/*.csproj|\
    src/Ro3Emu.LoadTest/*.csproj|tools/EconomyProbe/*.csproj|"$tests_dir"/*.csproj) broad "$f changes the build" ;;
    "$tests_dir"/*.cs|src/Ro3Emu.Server/*.cs|src/Ro3Emu.Bot/*.cs|src/Ro3Emu.LoadTest/*.cs|tools/EconomyProbe/*.cs|src/Ro3Emu.ClientHooks/*.cs)
      if [ ! -f "$f" ]; then broad "$f was deleted"
      elif [[ "$f" == "$tests_dir"/* ]] && is_test_file "$f"; then add_test_file "$f"
      else queue_types_of "$f"; fi ;;
    src/Ro3Emu.Server/*) queue_readers_of "$f" ;;
    *) ;; # client-side projects and tools no test project builds
  esac
}

# The test class named after a type: MailService -> MailTests or MailServiceTests.
add_owner_of() {
  local cand
  for cand in "${1}Tests" "${1%Service}Tests" "${1%Store}Tests"; do
    grep -qwE "class $cand" "$tests_dir"/*.cs 2>/dev/null && owners[$cand]=1
  done
  return 0
}

# Every test class whose file names a queued type; a helper file (no tests) queues its own types in turn.
resolve_classes() {
  local i=0 t file c
  while [ $i -lt ${#pending_types[@]} ]; do
    t=${pending_types[$i]}; i=$((i + 1))
    add_owner_of "$t"
    while read -r file; do
      [ -n "$file" ] || continue
      if is_test_file "$file"; then
        while read -r c; do [ -n "$c" ] && users[$c]=1; done < <(declared_classes "$file")
      else
        queue_types_of "$file"
      fi
    done < <(grep -lw -- "$t" "$tests_dir"/*.cs 2>/dev/null || true)
  done
  [ ${#pending_types[@]} -eq 0 ] || [ ${#owners[@]} -gt 0 ] || [ ${#users[@]} -gt 0 ] || broad "no test names the changed types"
}

if [ "$all" = 1 ]; then
  broad "--all"
else
  merge_base=$(git merge-base "$base" HEAD)
  while read -r f; do
    [ -n "$f" ] && classify "$f"
  done < <({ git diff --name-only "$merge_base"; git ls-files --others --exclude-standard; } | sort -u)
  [ -n "$broad_reason" ] || resolve_classes
fi

run() {
  if [ "$print" = 1 ]; then printf '%q ' "$@"; echo; else "$@"; fi
}

declare -A classes=()
for c in "${!owners[@]}"; do classes[$c]=1; done
for c in "${!users[@]}"; do classes[$c]=1; done
if [ -z "$broad_reason" ] && [ "$wide" = 0 ] && [ ${#classes[@]} -gt "$max" ]; then
  if [ ${#owners[@]} -gt 0 ]; then
    echo "${#classes[@]} test classes name the changed types; running the ${#owners[@]} named after them (--wide runs all)"
    classes=()
    for c in "${!owners[@]}"; do classes[$c]=1; done
  else
    broad "${#classes[@]} test classes name the changed types and none is named after them"
  fi
fi

if [ -n "$broad_reason" ]; then
  echo "CI set: $broad_reason"
  status=0
  for p in tests/Ro3Emu.Tests tests/Ro3Emu.Patcher.Tests tests/Ro3Emu.Setup.Tests; do
    [ "$build" = 1 ] && [ "$print" = 0 ] && dotnet build "$p" -c Release
    RO3EMU_NO_CLIENT_DATA=1 run dotnet test "$p" -c Release --no-build --filter "Category!=Slow" "${extra[@]}" || status=1
  done
  exit $status
fi

status=0
if [ ${#classes[@]} -gt 0 ]; then
  filter=""
  for c in $(printf '%s\n' "${!classes[@]}" | sort); do
    filter+="${filter:+|}FullyQualifiedName~Ro3Emu.Tests.$c."
  done
  echo "Server tests: ${#classes[@]} classes"
  [ "$build" = 1 ] && [ "$print" = 0 ] && dotnet build "$tests_dir" -c Release
  run dotnet test "$tests_dir" -c Release --no-build --filter "($filter)&Category!=Slow" "${extra[@]}" || status=1
fi
for p in "${!projects[@]}"; do
  [ "$build" = 1 ] && [ "$print" = 0 ] && dotnet build "$p" -c Release
  run dotnet test "$p" -c Release --no-build --filter "Category!=Slow" "${extra[@]}" || status=1
done
[ ${#classes[@]} -gt 0 ] || [ ${#projects[@]} -gt 0 ] || echo "No test covers these changes."
exit $status
