#!/usr/bin/env bash
# Restore a Plannit backup archive (made by backup-db.sh) into a data directory.
#
# Usage: restore-db.sh <archive> <data-dir> [--force]
#
# STOP THE APP FIRST. Restores plannit.db and keys/ into <data-dir>. Refuses to overwrite an existing
# database or key ring unless --force is given. The archive's checksum must match its .sha256 file
# and the restored database must pass an integrity check before anything is put in place.

set -euo pipefail
umask 077

ARCHIVE="${1:-}"
TARGET="${2:-}"
FORCE="${3:-}"

fail() { echo "RESTORE FAILED: $*" >&2; exit 1; }

[ -n "$ARCHIVE" ] && [ -n "$TARGET" ] || fail "usage: restore-db.sh <archive> <data-dir> [--force]"
command -v sqlite3 >/dev/null 2>&1 || fail "sqlite3 is not installed."
[ -f "$ARCHIVE" ] || fail "archive not found: $ARCHIVE"
[ -f "$ARCHIVE.sha256" ] || fail "checksum file not found: $ARCHIVE.sha256"
( cd "$(dirname "$ARCHIVE")" && sha256sum -c "$(basename "$ARCHIVE").sha256" >/dev/null ) || fail "checksum mismatch: the archive is damaged or was modified."

if [ "$FORCE" != "--force" ]; then
    [ ! -e "$TARGET/plannit.db" ] || fail "$TARGET/plannit.db already exists (use --force to overwrite)."
    [ ! -d "$TARGET/keys" ] || [ -z "$(ls -A "$TARGET/keys" 2>/dev/null)" ] || fail "$TARGET/keys is not empty (use --force to overwrite)."
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

SOURCE="$ARCHIVE"
case "$ARCHIVE" in
    *.gpg)
        command -v gpg >/dev/null 2>&1 || fail "archive is GPG-encrypted but gpg is not installed."
        gpg --batch --yes --output "$WORK/archive.tar.gz" --decrypt "$ARCHIVE" || fail "could not decrypt archive."
        SOURCE="$WORK/archive.tar.gz"
        ;;
esac

mkdir "$WORK/x"
tar -C "$WORK/x" -xzf "$SOURCE" || fail "could not extract archive."
[ -f "$WORK/x/plannit.db" ] || fail "archive has no plannit.db."
[ -d "$WORK/x/keys" ] && [ -n "$(ls -A "$WORK/x/keys")" ] || fail "archive has no key ring."

CHECK="$(sqlite3 "$WORK/x/plannit.db" "PRAGMA integrity_check;")" || fail "integrity check could not run."
[ "$CHECK" = "ok" ] || fail "restored database failed its integrity check: $CHECK"

mkdir -p "$TARGET"
rm -f "$TARGET/plannit.db" "$TARGET/plannit.db-wal" "$TARGET/plannit.db-shm"
cp "$WORK/x/plannit.db" "$TARGET/plannit.db"
rm -rf "$TARGET/keys"
mkdir -p "$TARGET/keys"
cp -a "$WORK/x/keys/." "$TARGET/keys/"
chmod 700 "$TARGET/keys"

# When run as root (needed to read a private backup directory from a container), hand the data to
# the image's non-root app user so the app can write it. A no-op where that user does not exist.
if [ "$(id -u)" = 0 ] && id app >/dev/null 2>&1; then
    chown -R app:app "$TARGET"
fi

echo "Restored $ARCHIVE into $TARGET"
echo "Next: start the app, then verify login and that integrations (AI key, bank sync) still decrypt."
