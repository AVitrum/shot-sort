# Rules shared by the pre-commit and pre-push hooks.
# Enable once per clone:  git config core.hooksPath .githooks

# Largest file that may be committed, in bytes.
MAX_FILE_BYTES=$((5 * 1024 * 1024))

# Prints the reason a path must not be committed; prints nothing if it is fine.
path_problem() {
    case "$1" in
        .claude/*|*/.claude/*|CLAUDE.md|*/CLAUDE.md|agent-rules.md)
            echo "agent files stay local" ;;
        plan.md)
            echo "local planning file" ;;
        .env|.env.*|*/.env|*/.env.*|secrets.json|*/secrets.json|*.pfx|*.p12|*.snk|*.pem|*.key|*.pubxml|*.publishsettings)
            echo "secret or credential file" ;;
        appsettings.*.json|*/appsettings.*.json)
            echo "personal settings override" ;;
        *.db|*.db-shm|*.db-wal|*.sqlite|*.sqlite3|*.log)
            echo "personal app data (decision log or logs)" ;;
        *.onnx|*.gguf|*.safetensors|*.part|*.bundle)
            echo "model or backup file" ;;
        */Assets/*|Assets/*)
            ;;
        *.png|*.jpg|*.jpeg|*.bmp|*.webp)
            echo "image, possibly a screenshot; artwork goes under an Assets folder" ;;
    esac
}

# Prints a label for a secret found in the text on stdin, or for this user's identity.
# The identity comes from the environment so this file holds nothing personal.
content_problem() {
    text=$(cat)
    if printf '%s' "$text" | grep -qE 'gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}'; then echo "GitHub token"; fi
    if printf '%s' "$text" | grep -qE 'hf_[A-Za-z0-9]{30,}'; then echo "Hugging Face token"; fi
    if printf '%s' "$text" | grep -qE 'sk-(ant-)?[A-Za-z0-9_-]{30,}'; then echo "API key"; fi
    if printf '%s' "$text" | grep -qE 'AKIA[0-9A-Z]{16}'; then echo "AWS key"; fi
    if printf '%s' "$text" | grep -qE -- '-----BEGIN [A-Z ]*PRIVATE KEY-----'; then echo "private key"; fi
    if printf '%s' "$text" | grep -qiE '(password|passwd|api[_-]?key|client[_-]?secret|auth[_-]?token)"?[[:space:]]*[:=][[:space:]]*["'"'"'][^"'"'"'{%$<][^"'"'"']{5,}["'"'"']'; then echo "hard-coded credential"; fi

    email=$(git config user.email)
    if [ -n "$email" ] && printf '%s' "$text" | grep -qiF "$email"; then echo "your email address"; fi
    user=${USERNAME:-$USER}
    if [ -n "$user" ] && printf '%s' "$text" | grep -qiE "[A-Za-z]:[\\\\/]+Users[\\\\/]+$user([\\\\/]|\$)|/c/Users/$user(/|\$)|/home/$user(/|\$)"; then echo "your personal folder path"; fi
}
