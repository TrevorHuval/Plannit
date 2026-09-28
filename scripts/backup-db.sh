#!/usr/bin/env bash
# Back up Plannit: a consistent SQLite snapshot AND the Data Protection key ring, in one archive.
#
# Usage: backup-db.sh [db-path] [keys-dir] [backup-dir]
#        defaults: /data/plannit.db  /data/keys  /data/backups
#
# Fails closed: it never copies a live database file. If sqlite3 is missing, the snapshot fails
# its integrity check, or there is no key ring, it exits non-zero and writes no archive. The key
# ring is required because it decrypts stored bank/API credentials; a database backup without it
# cannot restore those secrets. Treat the archive as a secret (created with umask 077).
#
# Environment:
#   BACKUP_KEEP            archives to retain (default 30)
#   BACKUP_GPG_RECIPIENT   encrypt the archive to this GPG key (fails if gpg is unavailable)
#
# Keep a copy OFF this host/volume, encrypted (see DEPLOY.md > Backups).

set -euo pipefail
umask 077

DB_PATH="${1:-/data/plannit.db}"
KEYS_DIR="${2:-/data/keys}"
BACKUP_DIR="${3:-/data/backups}"
KEEP="${BACKUP_KEEP:-30}"

fail() { echo "BACKUP FAILED: $*" >&2; exit 1; }

command -v sqlite3 >/dev/null 2>&1 || fail "sqlite3 is not installed; refusing to copy a live database file."
[ -f "$DB_PATH" ] || fail "database not found: $DB_PATH"
[ -d "$KEYS_DIR" ] && [ -n "$(ls -A "$KEYS_DIR" 2>/dev/null)" ] || fail "key ring missing or empty: $KEYS_DIR"
case "$KEEP" in ''|*[!0-9]*) fail "BACKUP_KEEP must be a positive integer";; esac
[ "$KEEP" -ge 1 ] || fail "BACKUP_KEEP must be at least 1"
if [ -n "${BACKUP_GPG_RECIPIENT:-}" ]; then
    command -v gpg >/dev/null 2>&1 || fail "BACKUP_GPG_RECIPIENT is set but gpg is not installed."
fi

mkdir -p "$BACKUP_DIR"
WORK="$(mktemp -d "$BACKUP_DIR/.work.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

# .backup uses SQLite's online backup API: consistent even while the app is writing (WAL included).
sqlite3 -cmd ".timeout 30000" "$DB_PATH" ".backup '$WORK/plannit.db'" || fail "sqlite3 .backup failed."

CHECK="$(sqlite3 "$WORK/plannit.db" "PRAGMA integrity_check;")" || fail "integrity check could not run."
[ "$CHECK" = "ok" ] || fail "snapshot failed its integrity check: $CHECK"

cp -a "$KEYS_DIR" "$WORK/keys"

STAMP="$(date -u +%Y%m%d_%H%M%SZ)"
ARCHIVE="$BACKUP_DIR/plannit_${STAMP}.tar.gz"
tar -C "$WORK" -czf "$ARCHIVE.partial" plannit.db keys || { rm -f "$ARCHIVE.partial"; fail "could not create archive."; }
mv "$ARCHIVE.partial" "$ARCHIVE"

if [ -n "${BACKUP_GPG_RECIPIENT:-}" ]; then
    gpg --batch --yes --trust-model always --encrypt --recipient "$BACKUP_GPG_RECIPIENT" --output "$ARCHIVE.gpg" "$ARCHIVE" \
        || { rm -f "$ARCHIVE" "$ARCHIVE.gpg"; fail "gpg encryption failed."; }
    rm -f "$ARCHIVE"
    ARCHIVE="$ARCHIVE.gpg"
fi

( cd "$BACKUP_DIR" && sha256sum "$(basename "$ARCHIVE")" > "$(basename "$ARCHIVE").sha256" )

# Retention: newest $KEEP archives (with their checksums) stay, everything older is removed.
{ ls -1t "$BACKUP_DIR"/plannit_*.tar.gz "$BACKUP_DIR"/plannit_*.tar.gz.gpg 2>/dev/null || true; } | tail -n +"$((KEEP + 1))" | while read -r old; do
    rm -f -- "$old" "$old.sha256"
done

echo "Backup saved to $ARCHIVE"
