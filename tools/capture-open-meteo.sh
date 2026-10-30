#!/usr/bin/env bash
# Captures a raw Open-Meteo /v1/forecast response as a test fixture for the fallback provider.
#
# Writes forecast.json (hourly precipitation_probability, precipitation, snowfall and
# weather_code as unix-time arrays) and capture.json under
# tests/Fixtures/open-meteo/<utc-timestamp>_<label>/.
#
# Uses a fixed public location, NOT PRECIP_LAT/LON: fixtures are committed to a public repo.
# Open-Meteo needs no User-Agent or API key.
#
# forecast_days=4 so a capture taken the day before a DST change covers the change night.
#
# Usage: tools/capture-open-meteo.sh [label]
#   tools/capture-open-meteo.sh pre-dst-fallback
set -euo pipefail

label="${1:-adhoc}"
lat="40.7128"     # Lower Manhattan, America/New_York; same point as capture-nws.sh
lon="-74.006"

repo_root="$(cd "$(dirname "$0")/.." && pwd)"

url="https://api.open-meteo.com/v1/forecast?latitude=${lat}&longitude=${lon}"
url+="&hourly=precipitation_probability,precipitation,snowfall,weather_code"
url+="&timeformat=unixtime&timezone=auto&forecast_days=4"

stamp="$(date -u +%Y-%m-%dT%H%MZ)"
out="$repo_root/tests/Fixtures/open-meteo/${stamp}_${label}"
mkdir -p "$out"

curl -fsS --retry 3 --retry-delay 5 "$url" -o "$out/forecast.json"

cat > "$out/capture.json" <<EOF
{
  "capturedAtUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "label": "$label",
  "url": "$url"
}
EOF

echo "Captured to ${out#"$repo_root"/}"
