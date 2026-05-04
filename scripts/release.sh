#!/usr/bin/env bash
set -euo pipefail

# release.sh
# Automates Context King GitHub releases:
# 1) validates clean state + auth
# 2) creates and pushes tag (vX.Y.Z)
# 3) waits for "Create release" workflow to complete
# 4) updates release notes after the release exists
#
# Usage:
#   bash scripts/release.sh --tag v1.7.3 --notes-file RELEASE_NOTES_v1.7.3.md
#   bash scripts/release.sh --notes-file RELEASE_NOTES_v1.7.3.md   # auto-increment latest tag

REPO="Fredrik-C/ContextKing"
WORKFLOW_NAME="Create release"
DEFAULT_TIMEOUT_SEC=1800
POLL_SEC=10

TAG=""
NOTES_FILE=""
TIMEOUT_SEC="$DEFAULT_TIMEOUT_SEC"
DRY_RUN=false

usage() {
  cat <<'EOF'
Usage:
  bash scripts/release.sh [--tag vX.Y.Z] --notes-file <file> [--timeout-sec <n>] [--dry-run]

Options:
  --tag <vX.Y.Z>       Release tag. If omitted, auto-increments latest v* tag (patch bump).
  --notes-file <file>  Markdown file used to overwrite release notes after release creation.
  --timeout-sec <n>    Max wait for workflow/release creation (default: 1800).
  --dry-run            Print planned actions without mutating git/GitHub.
  -h, --help           Show this help.
EOF
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "Error: required command not found: $1" >&2
    exit 1
  }
}

next_patch_tag() {
  local latest raw major minor patch
  latest="$(git tag --list 'v*' | sort -V | tail -n 1)"
  if [ -z "$latest" ]; then
    echo "v1.0.0"
    return
  fi

  raw="${latest#v}"
  IFS='.' read -r major minor patch <<<"$raw"
  if ! [[ "${major:-}" =~ ^[0-9]+$ && "${minor:-}" =~ ^[0-9]+$ && "${patch:-}" =~ ^[0-9]+$ ]]; then
    echo "Error: latest tag is not semver-like: $latest" >&2
    exit 1
  fi

  patch=$((patch + 1))
  echo "v${major}.${minor}.${patch}"
}

wait_for_workflow_run() {
  local tag="$1"
  local timeout_sec="$2"
  local started_at
  started_at="$(date +%s)"

  while true; do
    local run_id
    run_id="$(gh run list \
      --repo "$REPO" \
      --workflow "$WORKFLOW_NAME" \
      --branch "$tag" \
      --json databaseId \
      --jq '.[0].databaseId // empty')"

    if [ -n "$run_id" ]; then
      echo "Found workflow run id: $run_id"
      gh run watch "$run_id" --repo "$REPO" --interval "$POLL_SEC"
      return
    fi

    local now elapsed
    now="$(date +%s)"
    elapsed=$((now - started_at))
    if [ "$elapsed" -ge "$timeout_sec" ]; then
      echo "Error: timed out waiting for workflow run for $tag" >&2
      exit 1
    fi

    sleep "$POLL_SEC"
  done
}

wait_for_release_object() {
  local tag="$1"
  local timeout_sec="$2"
  local started_at
  started_at="$(date +%s)"

  while true; do
    if gh release view "$tag" --repo "$REPO" >/dev/null 2>&1; then
      return
    fi

    local now elapsed
    now="$(date +%s)"
    elapsed=$((now - started_at))
    if [ "$elapsed" -ge "$timeout_sec" ]; then
      echo "Error: timed out waiting for release object $tag" >&2
      exit 1
    fi

    sleep "$POLL_SEC"
  done
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --tag)
      [ "$#" -ge 2 ] || { echo "Error: --tag requires a value" >&2; exit 1; }
      TAG="$2"
      shift 2
      ;;
    --notes-file)
      [ "$#" -ge 2 ] || { echo "Error: --notes-file requires a value" >&2; exit 1; }
      NOTES_FILE="$2"
      shift 2
      ;;
    --timeout-sec)
      [ "$#" -ge 2 ] || { echo "Error: --timeout-sec requires a value" >&2; exit 1; }
      TIMEOUT_SEC="$2"
      shift 2
      ;;
    --dry-run)
      DRY_RUN=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Error: unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

require_cmd git
require_cmd gh

[ -n "$NOTES_FILE" ] || { echo "Error: --notes-file is required" >&2; exit 1; }
[ -f "$NOTES_FILE" ] || { echo "Error: notes file not found: $NOTES_FILE" >&2; exit 1; }

if [ -z "$TAG" ]; then
  TAG="$(next_patch_tag)"
fi

if ! [[ "$TAG" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Error: tag must match vX.Y.Z (got: $TAG)" >&2
  exit 1
fi

if [ -n "$(git status --porcelain)" ]; then
  echo "Error: working tree is not clean. Commit/stash changes first." >&2
  exit 1
fi

if ! gh auth status >/dev/null 2>&1; then
  echo "Error: GitHub CLI is not authenticated. Run: gh auth login" >&2
  exit 1
fi

if git rev-parse "$TAG" >/dev/null 2>&1; then
  echo "Error: local tag already exists: $TAG" >&2
  exit 1
fi

if git ls-remote --exit-code --tags origin "refs/tags/$TAG" >/dev/null 2>&1; then
  echo "Error: remote tag already exists: $TAG" >&2
  exit 1
fi

echo "Release plan:"
echo "  repo:        $REPO"
echo "  tag:         $TAG"
echo "  notes file:  $NOTES_FILE"
echo "  timeout sec: $TIMEOUT_SEC"

if [ "$DRY_RUN" = true ]; then
  echo "Dry-run enabled. No changes applied."
  exit 0
fi

git fetch --tags origin
git checkout main
git pull --ff-only origin main

git tag "$TAG"
git push origin "$TAG"

echo "Waiting for workflow: $WORKFLOW_NAME ($TAG)..."
wait_for_workflow_run "$TAG" "$TIMEOUT_SEC"

echo "Waiting for release object: $TAG..."
wait_for_release_object "$TAG" "$TIMEOUT_SEC"

echo "Updating release notes from: $NOTES_FILE"
gh release edit "$TAG" \
  --repo "$REPO" \
  --title "Context King $TAG" \
  --notes-file "$NOTES_FILE"

echo "Done. Release updated:"
gh release view "$TAG" --repo "$REPO"

