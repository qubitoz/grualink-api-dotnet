#!/usr/bin/env bash
# Runs both examples against local stand-ins, to prove they start and work end to end.
# Nothing here talks to the real API and every value is fake.
#
#   dotnet build --configuration Release && bash scripts/run-examples.sh
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIGURATION="${CONFIGURATION:-Release}"
API_PORT=18081
HOOK_PORT=18082
FAKE_KEY="glk_test_FAKE_KEY_FOR_CI_ONLY"
FAKE_SECRET="whsec_TEST_ONLY_FAKE_SECRET_CI"
HOOK_URL="http://127.0.0.1:${HOOK_PORT}/webhooks/grualink"
WORK="$(mktemp -d)"
PIDS=()

cleanup() {
  for pid in "${PIDS[@]}"; do kill "$pid" 2>/dev/null || true; done
  rm -rf "$WORK"
}
trap cleanup EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

wait_for() { # url, expected status
  for _ in $(seq 1 60); do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' "$1" || true)" = "$2" ]; then return 0; fi
    sleep 0.5
  done
  fail "$1 did not answer $2 in time"
}

bin() { echo "$1/bin/${CONFIGURATION}/net8.0/$2.dll"; }

echo "== Example 1: request a tow truck =="
PORT="$API_PORT" dotnet "$(bin tests/FakeApi FakeApi)" &
PIDS+=("$!")
wait_for "http://127.0.0.1:${API_PORT}/api/v1/health" 200

GRUALINK_API_KEY="$FAKE_KEY" GRUALINK_API_BASE_URL="http://127.0.0.1:${API_PORT}/api/v1" \
  dotnet "$(bin examples/RequestTow RequestTow)" | tee "$WORK/request.txt"
grep -q 'API status: ok (v1)' "$WORK/request.txt" || fail "no health line"
grep -q 'with status "confirmed"' "$WORK/request.txt" || fail "the request was not created as confirmed"
grep -q 'Total:     1002.62 MXN' "$WORK/request.txt" || fail "no quote"
grep -q 'replayed=True, same id=True' "$WORK/request.txt" || fail "the second call was not a replay"
grep -q 'Status now: "confirmed"' "$WORK/request.txt" || fail "no status line"
grep -Eq 'Requests with reference TEST-CONFIRMAR-[0-9a-f]{8}: 1$' "$WORK/request.txt" || fail "not found by reference"

echo "== Example 1 refuses a live key and a missing key =="
if GRUALINK_API_KEY="glk_live_FAKE_KEY_FOR_CI_ONLY" dotnet "$(bin examples/RequestTow RequestTow)" 2>"$WORK/live.txt"; then
  fail "the example ran with a live key"
fi
grep -q 'only runs with a TEST key' "$WORK/live.txt" || fail "unexpected message for a live key"
if env -u GRUALINK_API_KEY dotnet "$(bin examples/RequestTow RequestTow)" 2>/dev/null; then
  fail "the example ran without a key"
fi

echo "== Example 2: webhook receiver =="
if env -u GRUALINK_WEBHOOK_SECRET -u GRUALINK_WEBHOOK_SECRET_PREVIOUS dotnet "$(bin examples/WebhookServer WebhookServer)" 2>/dev/null; then
  fail "the receiver started without a secret"
fi

GRUALINK_WEBHOOK_SECRET="$FAKE_SECRET" PORT="$HOOK_PORT" \
  dotnet "$(bin examples/WebhookServer WebhookServer)" >"$WORK/hook.txt" 2>&1 &
PIDS+=("$!")
wait_for "$HOOK_URL" 405

EVENT_ID="cccccccc-0000-4000-8000-000000000001"
BODY='{"id": "'"$EVENT_ID"'", "object": "event", "type": "service_request.confirmed", "mode": "test", "created_at": "2026-11-18T10:05:01.000Z", "data": {"previous_status": "quoted", "service_request": {"id": "11111111-2222-4333-8444-555555555555", "status": "confirmed", "external_ref": "TEST-CONFIRMAR-ÑU"}}}'

# The signature is computed here with openssl, independently of the library under test.
sign() { # timestamp, body
  printf '%s.%s' "$1" "$2" | openssl dgst -sha256 -hmac "$FAKE_SECRET" -r | cut -d' ' -f1
}

deliver() { # body, signature header -> prints "<status> <response body>"
  curl -s -o "$WORK/response.txt" -w '%{http_code}' -X POST "$HOOK_URL" \
    -H 'Content-Type: application/json; charset=utf-8' \
    -H 'X-GruaLink-Event: service_request.confirmed' \
    -H "X-GruaLink-Delivery: ${EVENT_ID}" \
    -H 'X-GruaLink-Mode: test' \
    -H "X-GruaLink-Signature: $2" \
    --data-binary "$1"
  echo " $(cat "$WORK/response.txt")"
}

NOW="$(date +%s)"
HEADER="t=${NOW},v1=$(sign "$NOW" "$BODY")"

RESULT="$(deliver "$BODY" "$HEADER")"
[ "$RESULT" = '200 {"received":true}' ] || fail "signed delivery: $RESULT"

RESULT="$(deliver "$BODY" "$HEADER")"
[ "$RESULT" = '200 {"received":true,"duplicate":true}' ] || fail "repeated delivery: $RESULT"

RESULT="$(deliver "${BODY/confirmed/completed}" "$HEADER")"
[ "$RESULT" = '401 {"received":false,"error":"invalid_signature"}' ] || fail "tampered delivery: $RESULT"

OLD="$((NOW - 3600))"
RESULT="$(deliver "$BODY" "t=${OLD},v1=$(sign "$OLD" "$BODY")")"
[ "$RESULT" = '401 {"received":false,"error":"invalid_signature"}' ] || fail "stale delivery: $RESULT"

cat "$WORK/hook.txt"
grep -q 'Listening for GRUALINK webhooks' "$WORK/hook.txt" || fail "the receiver did not announce itself"
[ "$(grep -c 'service_request.confirmed: request 11111111-2222-4333-8444-555555555555 (TEST-CONFIRMAR-ÑU) is now "confirmed"' "$WORK/hook.txt")" = "1" ] \
  || fail "the event was not processed exactly once"
grep -q '401 (signature_mismatch)' "$WORK/hook.txt" || fail "the tampered delivery was not logged"
grep -q '401 (timestamp_out_of_tolerance)' "$WORK/hook.txt" || fail "the stale delivery was not logged"

echo "OK: both examples ran."
