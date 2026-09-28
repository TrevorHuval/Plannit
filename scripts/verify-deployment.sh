#!/usr/bin/env bash
# Read-only smoke checks against a deployed Plannit (audit "deployment verification checklist").
#
# Usage: scripts/verify-deployment.sh <base-url> [options]
#   <base-url>                 e.g. https://trevorhuval.com/plannit  (no trailing slash)
#   --registration open|closed expected registration mode (default: report only)
#   --backend <url>            a URL that must NOT be reachable from here, e.g. http://<server-ip>:8080/healthz
#   --check-rate-limit         send 12 rapid login-page requests (with a spoofed X-Forwarded-For) to see
#                              whether the auth rate limit still trips; this rate-limits YOUR IP for ~1 minute
#
# Sends only unauthenticated GET requests (no logins, no form posts, no account creation). It cannot
# see server-side settings (SMTP, backups, MFA, accounts); those are the manual items in DEPLOY.md.
# Exit status is non-zero if any check FAILs.

set -uo pipefail

BASE="${1:-}"
[ -n "$BASE" ] || { sed -n 2,14p "$0"; exit 2; }
shift
EXPECT_REG=""; BACKEND=""; RATE=0
while [ $# -gt 0 ]; do
    case "$1" in
        --registration) EXPECT_REG="${2:-}"; shift 2;;
        --backend) BACKEND="${2:-}"; shift 2;;
        --check-rate-limit) RATE=1; shift;;
        *) echo "unknown option: $1" >&2; exit 2;;
    esac
done
BASE="${BASE%/}"
command -v curl >/dev/null 2>&1 || { echo "curl is required" >&2; exit 2; }

FAILS=0
pass() { echo "PASS  $*"; }
fail() { echo "FAIL  $*"; FAILS=$((FAILS + 1)); }
info() { echo "INFO  $*"; }
CURL=(curl -sS -m 20 -o /dev/null)

PATHPART="$(echo "$BASE" | sed -E 's#^https?://[^/]+##')"
HOSTPART="$(echo "$BASE" | sed -E 's#^(https?://[^/]+).*#\1#')"

# 1. Plain HTTP must redirect to HTTPS.
if [[ "$BASE" == https://* ]]; then
    HTTP_URL="http://${BASE#https://}/healthz"
    CODE="$(curl -sS -m 20 -o /dev/null -w '%{http_code} %{redirect_url}' "$HTTP_URL" 2>/dev/null || true)"
    case "$CODE" in
        30[1278]\ https://*) pass "http redirects to https ($CODE)";;
        *) fail "http does not redirect to https (got: ${CODE:-no response})";;
    esac
fi

# 2. Health endpoint.
CODE="$(curl -sS -m 20 -o /dev/null -w '%{http_code}' "$BASE/healthz" 2>/dev/null || true)"
[ "$CODE" = 200 ] && pass "/healthz returns 200" || fail "/healthz returned ${CODE:-no response}"

# 3. Login page: status, headers, cookies, no scaffold text.
HEAD_FILE="$(mktemp)"; BODY_FILE="$(mktemp)"; trap 'rm -f "$HEAD_FILE" "$BODY_FILE"' EXIT
CODE="$(curl -sS -m 20 -D "$HEAD_FILE" -o "$BODY_FILE" -w '%{http_code}' "$BASE/Identity/Account/Login" 2>/dev/null || true)"
[ "$CODE" = 200 ] && pass "login page returns 200" || fail "login page returned ${CODE:-no response}"

header() { grep -i "^$1:" "$HEAD_FILE" | head -1; }
[ -n "$(header strict-transport-security)" ] && pass "HSTS header present" || fail "HSTS header missing"
header x-frame-options | grep -qi deny && pass "X-Frame-Options: DENY" || fail "X-Frame-Options DENY missing"
header x-content-type-options | grep -qi nosniff && pass "X-Content-Type-Options: nosniff" || fail "nosniff missing"
CSP="$(header content-security-policy)"
[ -n "$CSP" ] && pass "Content-Security-Policy present" || fail "Content-Security-Policy missing"
info "CSP form-action: $(echo "$CSP" | grep -oiE "form-action[^;]*" | head -1)"
echo "$CSP" | grep -q "unsafe-inline" && info "CSP still allows 'unsafe-inline' scripts (defense-in-depth item, not a defect by itself)"

COOKIES="$(grep -i '^set-cookie:' "$HEAD_FILE" || true)"
if [ -z "$COOKIES" ]; then
    info "no cookies set by the login page"
else
    while IFS= read -r line; do
        name="$(echo "$line" | sed -E 's/^[Ss]et-[Cc]ookie: *([^=]*)=.*/\1/')"
        echo "$line" | grep -qi ';[ ]*secure' && pass "cookie $name is Secure" \
            || fail "cookie $name is NOT Secure (the app may not see https: set Hosting__AssumeHttps=true or fix ForwardedHeaders)"
    done <<< "$COOKIES"
fi

grep -q "go.microsoft.com" "$BODY_FILE" && fail "login page still shows scaffold setup text" || pass "login page has no scaffold text"
grep -qi "apple" "$BODY_FILE" && info "login page mentions Apple (only expected if Apple sign-in is configured)"

# 4. Registration mode.
REG="$(curl -sS -m 20 -o /dev/null -w '%{http_code}' "$BASE/Identity/Account/Register" 2>/dev/null || true)"
case "$REG" in
    200) MODE=open;;
    403|404) MODE=closed;;
    *) MODE=unknown;;
esac
info "registration is $MODE (GET Register returned ${REG:-no response})"
if [ -n "$EXPECT_REG" ]; then
    [ "$MODE" = "$EXPECT_REG" ] && pass "registration mode matches expected ($EXPECT_REG)" || fail "registration is $MODE but expected $EXPECT_REG"
fi

# 5. Protected routes redirect to the PathBase-correct login.
for route in /Transactions /Reports /Settings /Projections; do
    OUT="$(curl -sS -m 20 -o /dev/null -w '%{http_code} %{redirect_url}' "$BASE$route" 2>/dev/null || true)"
    case "$OUT" in
        30[1-8]\ *"$PATHPART/Identity/Account/Login"*) pass "$route redirects anonymous users to login";;
        *) fail "$route did not redirect to $PATHPART/Identity/Account/Login (got: $OUT)";;
    esac
done

# 6. Privacy page.
CODE="$(curl -sS -m 20 -o "$BODY_FILE" -w '%{http_code}' "$BASE/Home/Privacy" 2>/dev/null || true)"
if [ "$CODE" = 200 ] && ! grep -q "Use this page to detail" "$BODY_FILE"; then pass "privacy page is published (no template text)"; else fail "privacy page missing or still the template (HTTP ${CODE:-none})"; fi

# 7. Backend must not be reachable around the proxy.
if [ -n "$BACKEND" ]; then
    if curl -sS -m 8 -o /dev/null "$BACKEND" 2>/dev/null; then
        fail "backend is reachable directly at $BACKEND (bind it to 127.0.0.1 or firewall the port)"
    else
        pass "backend is not reachable directly at $BACKEND"
    fi
else
    info "skipped backend-exposure check (pass --backend http://<server-ip>:8080/healthz)"
fi

# 8. Rate limit still trips when X-Forwarded-For is spoofed (proxy trust is pinned).
if [ "$RATE" = 1 ]; then
    LIMITED=0
    for i in $(seq 1 12); do
        C="$(curl -sS -m 20 -o /dev/null -w '%{http_code}' -H "X-Forwarded-For: 198.51.100.$i" "$BASE/Identity/Account/Login" 2>/dev/null || true)"
        [ "$C" = 429 ] && LIMITED=1
    done
    [ "$LIMITED" = 1 ] && pass "auth rate limit tripped despite spoofed X-Forwarded-For" \
        || fail "auth rate limit never tripped in 12 requests (forwarded headers may be trusted from anywhere)"
else
    info "skipped rate-limit/spoofing check (pass --check-rate-limit)"
fi

echo
if [ "$FAILS" -eq 0 ]; then echo "All checks passed."; else echo "$FAILS check(s) failed."; fi
[ "$FAILS" -eq 0 ]
