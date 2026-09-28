#!/usr/bin/env bash
# End-to-end backup/restore drill against a built Plannit image (audit P2-05).
#
# Usage: scripts/docker-backup-drill.sh [image]        (default image: plannit:ci)
#
# 1. Start the image with a fresh volume and let it create its database and key ring.
# 2. Write to the live database continuously while running the EXACT documented backup command.
# 3. Destroy the container and its volume (simulated loss of the original volume).
# 4. Restore the archive into a brand-new empty volume with the documented restore command.
# 5. Start the app on the restored volume; check health, SQLite integrity, migration history,
#    rows written before the backup, and that the key ring is byte-identical.
#
# Needs docker and curl on the host. Uses only throwaway containers, volumes and a temp dir.

set -euo pipefail

IMAGE="${1:-plannit:ci}"
SUFFIX="$$"
C1="plannit-drill-a-$SUFFIX"; C2="plannit-drill-b-$SUFFIX"
V1="plannit-drill-vol-a-$SUFFIX"; V2="plannit-drill-vol-b-$SUFFIX"
PORT="${DRILL_PORT:-18080}"
WORK="$(mktemp -d)"
WRITER=0

cleanup() {
    [ "$WRITER" != 0 ] && kill "$WRITER" 2>/dev/null || true
    docker rm -f "$C1" "$C2" >/dev/null 2>&1 || true
    docker volume rm -f "$V1" "$V2" >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

step() { echo; echo "== $*"; }
die()  { echo "DRILL FAILED: $*" >&2; docker logs "$C1" 2>&1 | tail -20 >&2 || true; docker logs "$C2" 2>&1 | tail -20 >&2 || true; exit 1; }

wait_healthy() {
    local name="$1"
    for _ in $(seq 1 60); do
        [ "$(docker inspect -f '{{.State.Health.Status}}' "$name" 2>/dev/null)" = healthy ] && return 0
        sleep 2
    done
    die "$name did not become healthy"
}

step "Start the original container"
docker volume create "$V1" >/dev/null
docker run -d --name "$C1" -p "127.0.0.1:$PORT:8080" -v "$V1:/data" "$IMAGE" >/dev/null
wait_healthy "$C1"
# Rendering a form makes the app create its Data Protection key ring.
curl -fsS "http://127.0.0.1:$PORT/Identity/Account/Login" >/dev/null || die "login page did not load"
docker exec "$C1" sh -c 'ls /data/keys | grep -q .' || die "no key ring was created"

step "Write to the live database during the backup"
docker exec "$C1" sqlite3 /data/plannit.db "CREATE TABLE drill_marker(id INTEGER PRIMARY KEY, v TEXT); INSERT INTO drill_marker(v) VALUES ('before');"
( while true; do docker exec "$C1" sqlite3 -cmd ".timeout 5000" /data/plannit.db "INSERT INTO drill_marker(v) VALUES (hex(randomblob(32)));" >/dev/null 2>&1 || true; done ) &
WRITER=$!
sleep 2

docker exec "$C1" sh -c 'cd /data/keys && sha256sum * | sort' > "$WORK/keys.sha"

step "Documented backup command"
docker exec "$C1" /bin/bash -c '/app/scripts/backup-db.sh /data/plannit.db /data/keys /data/backups' | tee "$WORK/backup.out"
ARCHIVE_IN_CONTAINER="$(sed -n 's/^Backup saved to //p' "$WORK/backup.out")"
[ -n "$ARCHIVE_IN_CONTAINER" ] || die "backup did not report an archive"
kill "$WRITER" 2>/dev/null || true; wait "$WRITER" 2>/dev/null || true; WRITER=0

docker cp "$C1:$ARCHIVE_IN_CONTAINER" "$WORK/"
docker cp "$C1:$ARCHIVE_IN_CONTAINER.sha256" "$WORK/"
ARCHIVE="$WORK/$(basename "$ARCHIVE_IN_CONTAINER")"

step "Lose the original volume"
docker rm -f "$C1" >/dev/null
docker volume rm "$V1" >/dev/null

step "Restore into an empty volume"
docker volume create "$V2" >/dev/null
# Root so it can read the private backup directory; restore-db.sh chowns the result to the app user.
docker run --rm --user root -v "$V2:/data" -v "$WORK:/in:ro" --entrypoint /app/scripts/restore-db.sh "$IMAGE" \
    "/in/$(basename "$ARCHIVE")" /data

step "Start the app on the restored volume"
docker run -d --name "$C2" -p "127.0.0.1:$PORT:8080" -v "$V2:/data" "$IMAGE" >/dev/null
wait_healthy "$C2"

[ "$(docker exec "$C2" sqlite3 /data/plannit.db 'PRAGMA integrity_check;')" = ok ] || die "integrity check failed after restore"
MIGRATIONS="$(docker exec "$C2" sqlite3 /data/plannit.db 'SELECT count(*) FROM __EFMigrationsHistory;')"
[ "$MIGRATIONS" -ge 1 ] || die "no migration history after restore"
[ "$(docker exec "$C2" sqlite3 /data/plannit.db "SELECT v FROM drill_marker WHERE id=1;")" = before ] || die "row written before the backup is missing"
ROWS="$(docker exec "$C2" sqlite3 /data/plannit.db 'SELECT count(*) FROM drill_marker;')"
[ "$ROWS" -ge 1 ] || die "no marker rows after restore"
docker exec "$C2" sh -c 'cd /data/keys && sha256sum * | sort' | diff - "$WORK/keys.sha" || die "key ring differs after restore"
curl -fsS "http://127.0.0.1:$PORT/Identity/Account/Login" >/dev/null || die "login page did not load after restore"

echo
echo "DRILL PASSED: restored $ROWS marker rows, $MIGRATIONS migrations, key ring identical."
