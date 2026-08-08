#!/usr/bin/env sh
# Install the upipe client. macOS, Linux, and WSL.
#
# The Unity side of this package installs through the Package Manager; upipe is
# the other half — the shell client that talks to the Pipeline HTTP server. It
# lives outside Unity, so it cannot ride along with the package import and needs
# a copy onto PATH.
#
#   ./install.sh                 # install to ~/.local/bin
#   ./install.sh /usr/local/bin  # or wherever you keep tools
#   ./install.sh --uninstall     # remove it again
set -eu

SOURCE_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SOURCE="$SOURCE_DIR/upipe"
DEFAULT_BIN="$HOME/.local/bin"

say()  { printf '%s\n' "$*"; }
warn() { printf '%s\n' "$*" >&2; }
die()  { printf 'install.sh: %s\n' "$*" >&2; exit 1; }

# --- uninstall -------------------------------------------------------------
if [ "${1:-}" = "--uninstall" ]; then
    # A directory given after --uninstall means exactly that directory. Scanning the
    # usual locations as well would delete an install the caller did not name — which
    # is a destructive surprise, and the caller has no way to ask for less.
    if [ -n "${2:-}" ]; then
        search=$2
    else
        search="$DEFAULT_BIN /usr/local/bin"
    fi

    removed=0
    for dir in $search; do
        if [ -f "$dir/upipe" ]; then
            rm -f "$dir/upipe" && say "removed $dir/upipe" && removed=1
        fi
    done

    if [ "$removed" -eq 0 ]; then
        say "upipe was not found in:$(printf ' %s' $search)"
    fi
    say "Credential caches, if any, are under ~/.config/upipe — delete that folder to clear them."
    exit 0
fi

BIN="${1:-$DEFAULT_BIN}"

[ -f "$SOURCE" ] || die "upipe not found next to this script (looked in $SOURCE_DIR)"

# --- dependencies ----------------------------------------------------------
# Checked before copying: an installed tool that fails on first use is worse than
# a refusal that says what is missing.
missing=
command -v curl    >/dev/null 2>&1 || missing="$missing curl"
command -v python3 >/dev/null 2>&1 || missing="$missing python3"
[ -z "$missing" ] || die "missing required command(s):$missing"

# --- install ---------------------------------------------------------------
mkdir -p "$BIN" || die "cannot create $BIN"
cp "$SOURCE" "$BIN/upipe" || die "cannot write to $BIN (try: sudo ./install.sh $BIN)"
chmod +x "$BIN/upipe"

say "installed $BIN/upipe"

# --- PATH ------------------------------------------------------------------
case ":$PATH:" in
    *":$BIN:"*) ;;
    *)
        warn ""
        warn "$BIN is not on your PATH. Add it:"
        warn ""
        case "${SHELL##*/}" in
            zsh)  warn "  echo 'export PATH=\"$BIN:\$PATH\"' >> ~/.zshrc && exec zsh" ;;
            fish) warn "  fish_add_path $BIN" ;;
            *)    warn "  echo 'export PATH=\"$BIN:\$PATH\"' >> ~/.bashrc && exec bash" ;;
        esac
        ;;
esac

# --- WSL note --------------------------------------------------------------
# The case this package was built for: Unity on Windows, the agent in WSL. The
# `unity` CLI cannot bridge that boundary because it validates the editor's PID,
# which WSL cannot see; upipe talks to the HTTP server instead, which crosses it
# fine. Worth stating so the setup does not look accidental.
if grep -qi microsoft /proc/version 2>/dev/null; then
    say ""
    say "WSL detected. Run upipe from inside the project directory on /mnt/... —"
    say "it walks up to find Library/Pipeline/.unity-pipeline-port, so no configuration is needed."
fi

say ""
say "Check it: upipe status   (from inside a Unity project, editor running with the Pipeline server started)"
