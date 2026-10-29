#!/usr/bin/env bash
# Captures raw api.weather.gov responses as test fixtures for the decision engine.
#
# Writes points.json, forecast-hourly.json and gridpoint.json (raw grid data with
# quantitativePrecipitation / snowfallAmount) under
# tests/Fixtures/nws/<utc-timestamp>_<label>/.
#
# Uses a fixed public location, NOT PRECIP_LAT/LON: fixtures are committed to a
# public repo, and a gridpoint polygon pins a location to ~2.5 km.
#
# Usage: tools/capture-nws.sh [label] [lat,lon]
#   tools/capture-nws.sh dst-fallback-eve
set -euo pipefail

label="${1:-adhoc}"
latlon="${2:-40.7128,-74.006}"   # Lower Manhattan, America/New_York

repo_root="$(cd "$(dirname "$0")/.." && pwd)"

if [[ -z "${PRECIP_USER_AGENT:-}" && -f "$repo_root/.env" ]]; then
  PRECIP_USER_AGENT="$(grep -E '^PRECIP_USER_AGENT=' "$repo_root/.env" | head -1 \
    | cut -d= -f2- | tr -d '\r' | sed -e 's/^"//' -e 's/"$//')"
fi
: "${PRECIP_USER_AGENT:?set PRECIP_USER_AGENT (NWS rejects requests without contact info)}"

stamp="$(date -u +%Y-%m-%dT%H%MZ)"
out="$repo_root/tests/Fixtures/nws/${stamp}_${label}"
mkdir -p "$out"

fetch() {
  curl -fsS --retry 3 --retry-delay 5 \
    -H "User-Agent: $PRECIP_USER_AGENT" \
    -H "Accept: application/geo+json" \
    "$1" -o "$2"
}

# Pull a top-level string URL out of the /points properties without needing jq.
json_url() {
  grep -o "\"$1\": *\"[^\"]*\"" "$2" | head -1 | sed -E 's/.*: *"([^"]*)"/\1/'
}

fetch "https://api.weather.gov/points/$latlon" "$out/points.json"

hourly_url="$(json_url forecastHourly "$out/points.json")"
grid_url="$(json_url forecastGridData "$out/points.json")"
[[ -n "$hourly_url" && -n "$grid_url" ]] || { echo "No forecast URLs in points.json" >&2; exit 1; }

fetch "$hourly_url" "$out/forecast-hourly.json"
fetch "$grid_url" "$out/gridpoint.json"

cat > "$out/capture.json" <<EOF
{
  "capturedAtUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "label": "$label",
  "latLon": "$latlon",
  "forecastHourly": "$hourly_url",
  "forecastGridData": "$grid_url"
}
EOF

echo "Captured to ${out#"$repo_root"/}"
