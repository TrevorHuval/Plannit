#!/usr/bin/env bash
# Backup the Plannit SQLite database.
# Usage: ./scripts/backup-db.sh [db-path] [backup-dir]
#
# Uses sqlite3 .backup for a safe, consistent copy even while the app is running.
# Falls back to a plain file copy if sqlite3 is not installed.

set -euo pipefail

DB_PATH="${1:-/data/plannit.db}"
BACKUP_DIR="${2:-/data/backups}"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
BACKUP_FILE="$BACKUP_DIR/plannit_$TIMESTAMP.db"

mkdir -p "$BACKUP_DIR"

if command -v sqlite3 &>/dev/null; then
    sqlite3 "$DB_PATH" ".backup '$BACKUP_FILE'"
else
    echo "WARNING: sqlite3 not found, falling back to file copy (ensure no writes are in progress)"
    cp "$DB_PATH" "$BACKUP_FILE"
fi

echo "Backup saved to $BACKUP_FILE"

# Keep only the last 30 backups
ls -1t "$BACKUP_DIR"/plannit_*.db 2>/dev/null | tail -n +31 | xargs -r rm --
