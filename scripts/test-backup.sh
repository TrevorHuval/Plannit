#!/usr/bin/env bash
# Behavioural test for backup-db.sh / restore-db.sh.
# Needs bash, sqlite3, tar, sha256sum (CI: ubuntu-latest). Usage: scripts/test-backup.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(mktemp -d)"
WRITER=0
trap 'kill "$WRITER" 2>/dev/null || true; rm -rf "$ROOT"' EXIT
pass() { echo "ok - $1"; }
die()  { echo "not ok - $1" >&2; exit 1; }

DATA="$ROOT/data"; mkdir -p "$DATA/keys"
echo "key-material-1" > "$DATA/keys/key-1.xml"
echo "key-material-2" > "$DATA/keys/key-2.xml"
sqlite3 "$DATA/plannit.db" "PRAGMA journal_mode=WAL; CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t(v) VALUES ('seed');" >/dev/null

# Writer that keeps inserting while the backup runs (also exercises WAL).
( while true; do sqlite3 -cmd ".timeout 5000" "$DATA/plannit.db" "INSERT INTO t(v) VALUES (hex(randomblob(64)));" 2>/dev/null || true; done ) &
WRITER=$!
sleep 1

BACKUPS="$ROOT/backups"
OUT="$("$HERE/backup-db.sh" "$DATA/plannit.db" "$DATA/keys" "$BACKUPS")" || die "backup during writes"
ARCHIVE="${OUT#Backup saved to }"
COUNT_AT_BACKUP_END="$(sqlite3 "$DATA/plannit.db" 'SELECT count(*) FROM t;')"
kill "$WRITER" 2>/dev/null || true; wait "$WRITER" 2>/dev/null || true; WRITER=0
[ -f "$ARCHIVE" ] && [ -f "$ARCHIVE.sha256" ] || die "archive and checksum exist"
# Windows filesystems (Git Bash) do not honour umask, so only assert modes on Linux.
if [ "$(uname -s)" = Linux ]; then [ "$(stat -c %a "$ARCHIVE")" = "600" ] || die "archive is private (0600)"; fi
pass "backup during concurrent writes"

NEW="$ROOT/restored"; mkdir -p "$NEW"
"$HERE/restore-db.sh" "$ARCHIVE" "$NEW" >/dev/null || die "restore into empty dir"
[ "$(sqlite3 "$NEW/plannit.db" 'PRAGMA integrity_check;')" = ok ] || die "integrity of restored db"
RESTORED="$(sqlite3 "$NEW/plannit.db" 'SELECT count(*) FROM t;')"
if [ "$RESTORED" -lt 1 ] || [ "$RESTORED" -gt "$COUNT_AT_BACKUP_END" ]; then die "row count plausible ($RESTORED of $COUNT_AT_BACKUP_END)"; fi
[ "$(sqlite3 "$NEW/plannit.db" "SELECT v FROM t WHERE id=1;")" = seed ] || die "seed row restored"
diff -r "$DATA/keys" "$NEW/keys" >/dev/null || die "key ring restored byte-for-byte"
pass "restore into empty dir (integrity, rows, keys)"

if "$HERE/restore-db.sh" "$ARCHIVE" "$NEW" >/dev/null 2>&1; then die "restore must refuse to overwrite"; fi
pass "restore refuses to overwrite without --force"
"$HERE/restore-db.sh" "$ARCHIVE" "$NEW" --force >/dev/null || die "restore with --force"
pass "restore --force overwrites"

# Fail closed: no sqlite3 on PATH => no archive, non-zero exit.
EMPTY_BIN="$ROOT/emptybin"; mkdir "$EMPTY_BIN"
for tool in bash mkdir mktemp cp tar rm date ls tail sha256sum dirname basename stat cat; do
    ln -s "$(command -v "$tool")" "$EMPTY_BIN/$tool"
done
BEFORE="$(ls "$BACKUPS" | wc -l)"
if PATH="$EMPTY_BIN" "$HERE/backup-db.sh" "$DATA/plannit.db" "$DATA/keys" "$BACKUPS" >/dev/null 2>&1; then die "backup must fail without sqlite3"; fi
[ "$(ls "$BACKUPS" | wc -l)" = "$BEFORE" ] || die "no archive written without sqlite3"
pass "fails closed without sqlite3"

mkdir "$ROOT/nokeys"
if "$HERE/backup-db.sh" "$DATA/plannit.db" "$ROOT/nokeys" "$BACKUPS" >/dev/null 2>&1; then die "backup must fail without keys"; fi
pass "fails closed without a key ring"

BEFORE="$(ls "$BACKUPS" | wc -l)"
head -c 4096 /dev/urandom > "$ROOT/corrupt.db"
if "$HERE/backup-db.sh" "$ROOT/corrupt.db" "$DATA/keys" "$BACKUPS" >/dev/null 2>&1; then die "backup must fail on a corrupt database"; fi
[ "$(ls "$BACKUPS" | wc -l)" = "$BEFORE" ] || die "no archive for corrupt db"
pass "fails closed on a corrupt database"

cp "$ARCHIVE" "$ROOT/t.tar.gz"; cp "$ARCHIVE.sha256" "$ROOT/t.tar.gz.sha256"
sed -i "s/  .*/  t.tar.gz/" "$ROOT/t.tar.gz.sha256"; printf 'x' >> "$ROOT/t.tar.gz"
mkdir "$ROOT/tamper"
if "$HERE/restore-db.sh" "$ROOT/t.tar.gz" "$ROOT/tamper" >/dev/null 2>&1; then die "restore must reject a tampered archive"; fi
[ ! -e "$ROOT/tamper/plannit.db" ] || die "nothing restored from a tampered archive"
pass "restore rejects a tampered archive"

for _ in 1 2 3 4; do sleep 1; BACKUP_KEEP=2 "$HERE/backup-db.sh" "$DATA/plannit.db" "$DATA/keys" "$BACKUPS" >/dev/null; done
[ "$(ls "$BACKUPS"/plannit_*.tar.gz | wc -l)" = 2 ] || die "retention keeps 2 archives"
[ "$(ls "$BACKUPS"/plannit_*.sha256 | wc -l)" = 2 ] || die "retention removes old checksums"
pass "retention"

echo "All backup script tests passed."
