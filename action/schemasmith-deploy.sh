#!/usr/bin/env bash
# SchemaSmith Deploy — GitHub Action entrypoint + resolution library.
# Sourced with --lib-only by unit tests; run directly by action.yml.
set -uo pipefail

resolve_rid() {
  local os="$1" arch="$2" o a
  case "$os" in
    Linux) o=linux ;; Windows) o=win ;; macOS) o=osx ;;
    *) echo "unsupported RUNNER_OS: $os" >&2; return 1 ;;
  esac
  case "$arch" in
    X64) a=x64 ;; ARM64) a=arm64 ;;
    *) echo "unsupported RUNNER_ARCH: $arch" >&2; return 1 ;;
  esac
  printf '%s-%s\n' "$o" "$a"
}

resolve_version() {
  local version_input="$1" action_ref="$2"
  if [ -n "$version_input" ]; then
    printf '%s\n' "$version_input"
  elif printf '%s' "$action_ref" | grep -qE '^v[0-9]'; then
    printf '%s\n' "$action_ref"
  else
    printf 'latest\n'
  fi
}

select_asset_url() {
  local release_json="$1" rid="$2"
  printf '%s' "$release_json" | jq -r --arg rid "$rid" \
    '.assets[] | select(.name | test("-" + $rid + "\\.(zip|tar\\.gz)$")) | .browser_download_url' \
    | head -n1
}

mode_switch() {
  case "$1" in
    # deploy and whatif both run SchemaQuench directly with no extra CLI switch;
    # whatif is applied as SmithySettings_WhatIfONLY in main(), not here.
    deploy|whatif) printf '\n' ;;
    validate) printf -- '--Validate\n' ;;
    test-connection) printf -- '--TestConnection\n' ;;
    preview-targets) printf -- '--PreviewTargets\n' ;;
    drift) printf -- '--Drift\n' ;;
    *) echo "unsupported mode: $1" >&2; return 1 ;;
  esac
}

# Workflow-command escaping: a message's data, and a property value, which also takes ':' and ','.
escape_data() {
  local s="$1"
  s="${s//'%'/%25}"
  s="${s//$'\r'/%0D}"
  s="${s//$'\n'/%0A}"
  printf '%s' "$s"
}

escape_property() {
  local s
  s="$(escape_data "$1")"
  s="${s//:/%3A}"
  s="${s//,/%2C}"
  printf '%s' "$s"
}

# A runner path made relative to the workspace, with '/' separators, so an annotation lands on the file in the pull
# request; empty when the path is not inside the workspace. Windows paths compare without regard to case.
workspace_relative() {
  local path="${1//\\//}" workspace="${2//\\//}"
  workspace="${workspace%/}"
  [ -n "$path" ] && [ -n "$workspace" ] || return 0
  # A relative path is relative to the directory the action runs in, the workspace.
  case "$path" in
    /*|[A-Za-z]:*) ;;
    *)
      while [ "${path#./}" != "$path" ]; do path="${path#./}"; done
      case "$path" in ..|../*) return 0 ;; esac
      printf '%s' "$path"; return 0 ;;
  esac
  local p="$path" w="$workspace"
  case "$w" in [A-Za-z]:*) p="${p,,}"; w="${w,,}" ;; esac
  case "$p" in "$w"/*) printf '%s' "${path:${#workspace}+1}" ;; esac
}

# At most ten of a kind: GitHub shows ten errors and ten warnings per step.
ANNOTATION_LIMIT=10

annotations_from_summary() {
  local summary="$1" workspace="$2" total scope message path file props
  if ! command -v jq >/dev/null 2>&1; then
    echo "::notice::jq is not installed on this runner, so the run's failures are not shown as annotations; they are in the deployment summary."
    return 0
  fi
  [ -f "$summary" ] || return 0

  total="$(jq '(.failures // []) | length' "$summary")"
  while IFS= read -r -d '' scope && IFS= read -r -d '' message; do
    path="$(printf '%s' "$message" | sed -n "s/^Unable to quench '\([^']*\)'.*/\1/p")"
    file="$(workspace_relative "$path" "$workspace")"
    props="title=$(escape_property "$scope")"
    [ -n "$file" ] && props="file=$(escape_property "$file"),$props"
    echo "::error $props::$(escape_data "$message")"
  done < <(jq -j --argjson n "$ANNOTATION_LIMIT" \
    '(.failures // [])[:$n][] | (.scopeKey // ""), "\u0000", ((.error // "") | split("\n")[0] | rtrimstr("\r")), "\u0000"' "$summary")
  [ "$total" -gt "$ANNOTATION_LIMIT" ] && echo "::notice::$((total - ANNOTATION_LIMIT)) more failures are in the deployment summary."

  local warnings=() warning
  while IFS= read -r -d '' warning; do warnings+=("$warning"); done < <(jq -j '
    ( if .drift.drifted == true then (.drift.targets // [])[] | select(.status == "drifted")
        | "Drift: [\(.server)].[\(.database)]\(if (.schema // "") != "" then " [Schema: \(.schema)]" else "" end) has \(.counted) change(s) a deploy would make", "\u0000"
      else empty end ),
    ( (.unsupportedDowngrade.downgrades // [])[] | "Downgraded on the target: \(.objectName) - \(.feature)", "\u0000" ),
    ( (.destructive.changes // [])[] | select(.outcome == "refused" or .outcome == "wouldRefuse")
        | "Refused by DestructiveChangePolicy: \(.kind) \(.objectName) on [\(.server)].[\(.database)]", "\u0000" )' "$summary")
  local i
  for ((i = 0; i < ${#warnings[@]} && i < ANNOTATION_LIMIT; i++)); do
    echo "::warning::$(escape_data "${warnings[$i]}")"
  done
  [ "${#warnings[@]}" -gt "$ANNOTATION_LIMIT" ] && echo "::notice::$((${#warnings[@]} - ANNOTATION_LIMIT)) more warnings are in the deployment summary."
  return 0
}

TRUNCATED_POINTER=$'\n\n_The full summary is in the log directory (the action'"'"'s log-dir output)._\n'

# Appends the Markdown summary to the job page; GitHub caps a step's summary at 1 MiB.
write_step_summary() {
  local source="$1" dest="$2" limit="${3:-921600}"
  [ -f "$source" ] && [ -n "$dest" ] || return 0
  if [ "$(wc -c < "$source")" -le "$limit" ]; then
    cat "$source" >> "$dest"
  else
    { head -c "$limit" "$source"; printf '%s' "$TRUNCATED_POINTER"; } >> "$dest"
  fi
}

# The pull-request comment: a hidden marker first, so a re-run edits this comment instead of adding one, then the
# summary, cut to GitHub's 65,536-character limit.
pr_comment_body() {
  local source="$1" marker="$2" limit="${3:-65536}" body
  body="$marker"$'\n'"$(cat "$source")"
  if [ "${#body}" -gt "$limit" ]; then
    body="${body:0:$((limit - ${#TRUNCATED_POINTER}))}$TRUNCATED_POINTER"
  fi
  printf '%s' "$body"
}

# The id of the first comment in a page of them (JSON on stdin) whose body starts with the marker; empty when none does.
comment_with_marker() {
  jq -r --arg m "$1" '[.[] | select((.body // "") | startswith($m))][0].id // empty' 2>/dev/null
}

# When sourced with --lib-only, expose functions and stop (no main run).
if [ "${1:-}" = "--lib-only" ]; then return 0 2>/dev/null || exit 0; fi

gh_api() { # $1 = url ; authenticated if GH_TOKEN present
  local auth=()
  [ -n "${GH_TOKEN:-}" ] && auth=(-H "Authorization: Bearer $GH_TOKEN")
  curl -sfL ${auth[@]+"${auth[@]}"} -H "Accept: application/vnd.github+json" "$1"
}

# Posts the summary on the pull request, or edits the comment an earlier run of the same mode and package posted.
post_pr_comment() {
  local md="$1" mode="$2" pr repo api marker payload existing auth
  case "${GITHUB_EVENT_NAME:-}" in
    pull_request|pull_request_target) ;;
    *) echo "::notice::pr-comment is on, but this run is not for a pull request, so no comment was posted."; return 0 ;;
  esac
  command -v jq >/dev/null 2>&1 || { echo "::notice::jq is not installed on this runner, so the summary was not posted as a comment."; return 0; }
  pr="$(jq -r '.pull_request.number // empty' "${GITHUB_EVENT_PATH:-/dev/null}" 2>/dev/null)"
  [ -n "$pr" ] && [ -n "${GH_TOKEN:-}" ] || return 0
  repo="$GITHUB_REPOSITORY"; api="${GITHUB_API_URL:-https://api.github.com}"
  marker="<!-- schemasmith-deploy:$mode:${SS_PRODUCT_PATH:-.} -->"
  payload="$(jq -n --arg body "$(pr_comment_body "$md" "$marker")" '{body: $body}')"
  auth=(-H "Authorization: Bearer $GH_TOKEN" -H "Accept: application/vnd.github+json")
  # A busy pull request has more comments than one page holds; the marker may be on any of them.
  local page body
  existing=""
  for page in $(seq 1 30); do
    body="$(curl -sf "${auth[@]}" "$api/repos/$repo/issues/$pr/comments?per_page=100&page=$page")" || break
    existing="$(printf '%s' "$body" | comment_with_marker "$marker")"
    [ -n "$existing" ] && break
    [ "$(printf '%s' "$body" | jq 'length' 2>/dev/null)" = "100" ] || break
  done
  if [ -n "$existing" ]; then
    curl -sf -X PATCH "${auth[@]}" "$api/repos/$repo/issues/comments/$existing" -d "$payload" >/dev/null
  else
    curl -sf -X POST "${auth[@]}" "$api/repos/$repo/issues/$pr/comments" -d "$payload" >/dev/null
  fi || echo "::notice::The summary could not be posted on the pull request: the workflow needs 'pull-requests: write', which a pull request from a fork cannot have."
  return 0
}

main() {
  local repo="Schema-Smith/SchemaSmith"
  local mode="${SS_MODE:-deploy}"
  local rid version rel_json url work bin logdir sw summary summary_json code pkg_win dest_win

  # required-input validation per mode
  if [ "$mode" != "validate" ] && [ -z "${SS_SERVER:-}" ]; then
    echo "::error::mode '$mode' requires 'server'"; exit 1
  fi
  if [ ! -e "${SS_PRODUCT_PATH:-.}" ]; then
    echo "::error::product-path '${SS_PRODUCT_PATH:-.}' does not exist"; exit 1
  fi

  rid="$(resolve_rid "$RUNNER_OS" "$RUNNER_ARCH")" || exit 1
  version="$(resolve_version "${SS_VERSION:-}" "${SS_ACTION_REF:-}")"
  echo "Resolving SchemaSmith $version for $rid"
  if [ "$version" = "latest" ]; then
    rel_json="$(gh_api "https://api.github.com/repos/$repo/releases/latest")"
  else
    rel_json="$(gh_api "https://api.github.com/repos/$repo/releases/tags/$version")"
  fi
  url="$(select_asset_url "$rel_json" "$rid")"
  [ -n "$url" ] || { echo "::error::no release asset for rid $rid (version $version)"; exit 1; }

  work="$(mktemp -d)"
  echo "Downloading $url"
  curl -sfL "$url" -o "$work/pkg" || { echo "::error::download failed: $url"; exit 1; }
  case "$url" in
    *.tar.gz) tar -xzf "$work/pkg" -C "$work" ;;
    *.zip)
      if command -v unzip >/dev/null 2>&1; then
        unzip -q "$work/pkg" -d "$work"
      else
        # PowerShell fallback (Windows without unzip): translate the git-bash POSIX
        # paths to Windows paths so Expand-Archive resolves them.
        pkg_win="$(cygpath -w "$work/pkg")"; dest_win="$(cygpath -w "$work")"
        powershell -NoProfile -Command "Expand-Archive -Path '$pkg_win' -DestinationPath '$dest_win' -Force"
      fi ;;
    *) echo "::error::unrecognized archive: $url"; exit 1 ;;
  esac

  bin="$(find "$work" -maxdepth 2 \( -name 'SchemaQuench' -o -name 'SchemaQuench.exe' \) | head -n1)"
  [ -n "$bin" ] || { echo "::error::SchemaQuench binary not found in archive"; exit 1; }
  chmod +x "$bin" 2>/dev/null || true

  logdir="$RUNNER_TEMP/schemasmith-logs"; mkdir -p "$logdir"
  sw="$(mode_switch "$mode")" || exit 1

  # config via env (password never on argv); package path + mode + extra-args
  export SmithySettings_SchemaPackagePath="${SS_PRODUCT_PATH:-.}"
  [ -n "${SS_SERVER:-}" ] && export SmithySettings_Target__Server="$SS_SERVER"
  [ -n "${SS_USER:-}" ] && export SmithySettings_Target__User="$SS_USER"
  [ -n "${SS_PASSWORD:-}" ] && export SmithySettings_Target__Password="$SS_PASSWORD"
  [ "$mode" = "whatif" ] && export SmithySettings_WhatIfONLY=true

  # shellcheck disable=SC2086  # intentional word-splitting of $sw and extra-args
  "$bin" $sw --LogPath="$logdir" ${SS_EXTRA_ARGS:-}
  code=$?

  summary=""; summary_json=""
  [ -f "$logdir/SchemaQuench - Summary.md" ] && summary="$logdir/SchemaQuench - Summary.md"
  [ -f "$logdir/SchemaQuench - Summary.json" ] && summary_json="$logdir/SchemaQuench - Summary.json"
  {
    echo "exit-code=$code"
    echo "log-dir=$logdir"
    echo "summary-path=$summary"
    echo "summary-json-path=$summary_json"
  } >> "$GITHUB_OUTPUT"

  # Reporting never changes the result: each step is allowed to fail on its own.
  [ -n "$summary_json" ] && { annotations_from_summary "$summary_json" "${GITHUB_WORKSPACE:-$PWD}" || true; }
  if [ "${SS_STEP_SUMMARY:-true}" != "false" ] && [ -n "$summary" ] && [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    write_step_summary "$summary" "$GITHUB_STEP_SUMMARY" || true
  fi
  if [ "${SS_PR_COMMENT:-false}" = "true" ] && [ -n "$summary" ]; then
    post_pr_comment "$summary" "$mode" || true
  fi
  exit "$code"
}
main "$@"
