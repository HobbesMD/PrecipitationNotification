#!/usr/bin/env bash
# Refuses an automated deploy that would put new code in front of an
# un-migrated schema (or old code in front of a newer one).
#
# Usage: deploy/azure/migration-guard.sh <deployed-image-ref> <target-sha> <true|false>
#   deployed-image-ref  what the job runs now, e.g. ghcr.io/hobbesmd/precipitation-notification:<sha>
#   target-sha          the commit about to be deployed (7-40 hex)
#   true|false          migrations_applied: the operator has already run `Worker --migrate`
#
# Run from the repo root (CI does). Needs full history: a shallow clone can't see
# the deployed commit and the guard stops rather than guess.
#
# Exit codes:
#   0  safe: already deployed, no migration files changed, or explicitly overridden
#   3  blocked: migration files changed between the two commits; next steps printed
#   1  anything else (bad arguments, an unmappable ref, a commit git can't find,
#      a git error). Fail closed (D24): "couldn't tell" is never read as "safe".
#
# Why this exists (D28): migrations are explicit. The job never migrates on
# startup, and a run against an out-of-date schema exits 6. That protects the
# data but still costs the night's alert. Continuous deployment would make that
# the default outcome of merging a migration, so the deploy itself checks first.
#
# The diff is direction-agnostic (`git diff A B` lists the same paths as
# `git diff B A`), so a rollback across a migration is blocked too: old code
# against a newer schema needs a human to confirm it is compatible.
set -euo pipefail

# Overridable for test-migration-guard.sh only; production always watches the
# real migrations folder.
MIGRATIONS_PATH="${MIGRATIONS_PATH:-src/Persistence/Migrations/}"

if [[ $# -ne 3 ]]; then
  echo "Usage: $0 <deployed-image-ref> <target-sha> <true|false>" >&2
  exit 1
fi
deployed_ref="$1"
target_sha="$2"
migrations_applied="$3"

case "$migrations_applied" in
  true)
    echo "Migration guard overridden: migrations_applied=true (the operator has run Worker --migrate)."
    exit 0
    ;;
  false) ;;
  *)
    echo "migrations_applied must be 'true' or 'false', got '$migrations_applied'." >&2
    exit 1
    ;;
esac

# The deployed commit comes from the image tag. A digest names image bytes, not a
# commit, so there's nothing to diff against: a human has to check. Any digest in
# the ref counts, even alongside a tag (repo:tag@sha256:...), because the digest
# is what was pulled and the tag beside it is unverified.
last_segment="${deployed_ref##*/}"
if [[ "$deployed_ref" == *@sha256:* ]]; then
  echo "The deployed image '$deployed_ref' is pinned by digest, which can't be mapped to a commit." >&2
  echo "Check by hand whether src/Persistence/Migrations/ changed since the deployed commit;" >&2
  echo "apply migrations if needed, then re-run with migrations_applied=true." >&2
  exit 1
fi
if [[ "$last_segment" != *:* || ! "${last_segment##*:}" =~ ^[0-9a-f]{7,40}$ ]]; then
  echo "The deployed image '$deployed_ref' has no git sha tag (:<7-40 hex>); a mutable tag can't be mapped to a commit." >&2
  exit 1
fi
deployed_sha="${last_segment##*:}"

# Same rule for the target: hex only. This also keeps anything that git would
# read as an option or a ref name (HEAD, main, --foo) out of the git calls below.
if [[ ! "$target_sha" =~ ^[0-9a-f]{7,40}$ ]]; then
  echo "target-sha must be 7-40 lowercase hex, got '$target_sha'." >&2
  exit 1
fi

if ! git rev-parse --git-dir >/dev/null 2>&1; then
  echo "Not inside a git repository (run from the repo root)." >&2
  exit 1
fi

# `^{commit}` makes rev-parse insist on a commit, and --verify fails on an
# abbreviation that matches more than one object, so an ambiguous short sha is
# an error, never a guess.
resolve() {
  git rev-parse --verify --quiet "$1^{commit}"
}
if ! deployed_full=$(resolve "$deployed_sha"); then
  echo "Can't find the deployed commit $deployed_sha in this clone (missing, ambiguous," >&2
  echo "a shallow clone, or rewritten history). Stopping rather than guessing." >&2
  exit 1
fi
if ! target_full=$(resolve "$target_sha"); then
  echo "Can't find the target commit $target_sha in this clone (missing, ambiguous," >&2
  echo "a shallow clone, or rewritten history). Stopping rather than guessing." >&2
  exit 1
fi

if [[ "$deployed_full" == "$target_full" ]]; then
  echo "Commit ${target_full:0:12} is already deployed; no schema change possible."
  exit 0
fi

# Assigned separately so a git failure isn't mistaken for "no files" (set -e
# does not apply inside an `if` condition, so check it explicitly).
if ! changed=$(git diff --name-only "$deployed_full" "$target_full" -- "$MIGRATIONS_PATH"); then
  echo "git diff failed between ${deployed_full:0:12} and ${target_full:0:12}; stopping." >&2
  exit 1
fi

if [[ -z "$changed" ]]; then
  echo "No migration changes between ${deployed_full:0:12} (deployed) and ${target_full:0:12} (target)."
  exit 0
fi

target_short="${target_full:0:12}"
{
  echo "Deploy blocked: migration files differ between ${deployed_full:0:12} (deployed) and $target_short (target):"
  while IFS= read -r file; do
    echo "  $file"
  done <<<"$changed"
} >&2
cat >&2 <<EOF

The job never migrates itself (D28); a run against an out-of-date schema exits 6.
Next steps:
  1. Apply migrations: Worker --migrate, per deploy/azure/README.md#migrate.
     If this is a rollback, confirm the older code works against the newer schema instead.
  2. Re-run the deploy via workflow_dispatch with sha=$target_short and migrations_applied=true.
EOF
exit 3
