#!/usr/bin/env bash
# Enforces the line-coverage threshold from docs/REQUIREMENTS.md ("Testing": >85% line coverage).
# Usage: scripts/check-coverage.sh <results-directory> <minimum-percent>
set -euo pipefail

results_dir="${1:-TestResults}"
minimum="${2:-85}"

mapfile -t reports < <(find "$results_dir" -name 'coverage.cobertura.xml' -print)

if [ "${#reports[@]}" -eq 0 ]; then
  echo "No cobertura coverage reports found under '$results_dir'." >&2
  exit 1
fi

covered=0
valid=0
for report in "${reports[@]}"; do
  # Cobertura's root <coverage> element carries lines-covered / lines-valid totals.
  line=$(grep -o 'lines-covered="[0-9]*" lines-valid="[0-9]*"' "$report" | head -n 1 || true)
  if [ -z "$line" ]; then
    echo "Could not read line totals from '$report'." >&2
    exit 1
  fi
  c=$(echo "$line" | sed -E 's/.*lines-covered="([0-9]*)".*/\1/')
  v=$(echo "$line" | sed -E 's/.*lines-valid="([0-9]*)".*/\1/')
  covered=$((covered + c))
  valid=$((valid + v))
done

if [ "$valid" -eq 0 ]; then
  echo "Coverage report contains no coverable lines." >&2
  exit 1
fi

percent=$(awk -v c="$covered" -v v="$valid" 'BEGIN { printf "%.2f", (c * 100) / v }')
echo "Line coverage: ${percent}% (${covered}/${valid}), minimum ${minimum}%."

awk -v p="$percent" -v m="$minimum" 'BEGIN { exit (p + 0 >= m + 0) ? 0 : 1 }' || {
  echo "Line coverage ${percent}% is below the required ${minimum}%." >&2
  exit 1
}
