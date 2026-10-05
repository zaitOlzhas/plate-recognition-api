#!/usr/bin/env sh
# Sends sample-payload.json to the webhook. Usage: ./send-sample.sh [base-url]   (API_KEY env var optional)
BASE_URL="${1:-http://localhost:5080}"
curl -sS -X POST "$BASE_URL/api/events/plate" \
  -H "Content-Type: application/json" \
  ${API_KEY:+-H "X-Api-Key: $API_KEY"} \
  --data-binary @"$(dirname "$0")/sample-payload.json"
echo
