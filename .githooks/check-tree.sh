#!/bin/sh
# Applies the commit rules to every tracked file. CI runs this, because hooks only work where a clone has enabled them.
. "$(git rev-parse --show-toplevel)/.githooks/safety-rules.sh"

failed=0
report() {
    echo "BLOCKED: $1: $2" >&2
    failed=1
}

files=$(mktemp)
trap 'rm -f "$files"' EXIT
git ls-files > "$files"

while IFS= read -r path; do
    problem=$(path_problem "$path")
    [ -z "$problem" ] || report "$path" "$problem"

    size=$(wc -c < "$path")
    [ "$size" -le "$MAX_FILE_BYTES" ] || report "$path" "$size bytes, limit is $MAX_FILE_BYTES"

    # The hooks describe the patterns they look for; everything else is scanned.
    case "$path" in
        .githooks/*) continue ;;
    esac
    found=$(content_problem < "$path")
    [ -z "$found" ] || report "$path" "contains $found"
done < "$files"

[ "$failed" -eq 0 ] || exit 1
echo "Checked $(wc -l < "$files") tracked files."
