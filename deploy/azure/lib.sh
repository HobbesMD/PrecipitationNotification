# shellcheck shell=bash
# Shared helpers for deploy/azure/*.sh. Sourced, never executed: it sets no shell
# options of its own, so the caller's `set -euo pipefail` is what applies.
#
# deploy.sh (first deploy, full configuration) and deploy-image.sh (image-only
# release from CI) must agree on what an acceptable IMAGE is and on what
# service.version it reports. One copy here means they cannot drift apart.

# validate_image IMAGE
# The job must pin a commit so every run is traceable to one: a git sha tag
# (7-40 hex) or a digest. Branch-like tags (:main, :latest) are mutable on GHCR.
# Take the last path segment, so a registry port (host:5000/img) isn't read as a tag.
# Exits the calling script (status 1) on a bad reference: fail closed (D24).
validate_image() {
  local image="$1"
  local last_segment="${image##*/}"
  if [[ "$image" =~ @sha256:[0-9a-f]{64}$ ]]; then
    :  # digest reference: immutable by construction
  elif [[ "$last_segment" == *:* && "${last_segment##*:}" =~ ^[0-9a-f]{7,40}$ ]]; then
    :  # git sha tag
  else
    echo "IMAGE must end in a git sha tag (:<7-40 hex>) or @sha256:<digest>, so a run maps to a commit." >&2
    exit 1
  fi
}

# image_service_version IMAGE
# Prints service.version for telemetry: the image's git sha tag, or the digest's
# hex for a digest reference. Call validate_image first; both accepted forms are
# then [0-9a-f] only. That matters: OTEL_RESOURCE_ATTRIBUTES is a
# comma/equals-delimited list, and a value containing either would corrupt it.
image_service_version() {
  local image="$1"
  local last_segment="${image##*/}"
  if [[ "$image" =~ @sha256:([0-9a-f]{64})$ ]]; then
    printf '%s\n' "${BASH_REMATCH[1]}"
  else
    printf '%s\n' "${last_segment##*:}"
  fi
}
