#!/bin/bash
# Boundary rules for the mod. Run after every build; non-zero exit means the stage is not passed.
#  1. il2cpp interop types may only be named inside Core/GameAPI
#  2. Core/GameAPI may not touch the transport: no sockets, no MpHost/MpClient/PeerLink
#
# Rule 2 used to forbid PvzMP.Core.Session inside Core/GameAPI as well. The customer's Stage 4 decision
# ("no IGameWorld boundary: the game side calls the room directly") made the session the seam instead, so
# the gameplay half names MpSession and its payloads by design; what still may not appear is the raw
# transport. Everything below that line is the part that keeps the two halves separable for the tests.
set -u
cd "$(dirname "$0")/.." || exit 2

GREP_COMMON=(--include='*.cs' --exclude-dir=ref --exclude-dir=tools --exclude-dir=bin --exclude-dir=obj)
PATTERN_OUTSIDE='\bIl2Cpp\.|\bIl2CppInterop|\bIl2CppUnityEngine|using Il2Cpp'
PATTERN_INSIDE='System\.Net|PvzMP\.Net|MpHost|MpClient|PeerLink'

fail=0

leaks=$(grep -rlE "$PATTERN_OUTSIDE" "${GREP_COMMON[@]}" . | grep -v '^\./Core/GameAPI/' || true)
if [ -n "$leaks" ]; then
  echo "BOUNDARY FAIL: il2cpp interop used outside Core/GameAPI:"
  echo "$leaks"
  fail=1
fi

pollution=$(grep -rlE "$PATTERN_INSIDE" "${GREP_COMMON[@]}" ./Core/GameAPI 2>/dev/null || true)
if [ -n "$pollution" ]; then
  echo "BOUNDARY FAIL: the transport is used inside Core/GameAPI:"
  echo "$pollution"
  fail=1
fi

if [ "$fail" -eq 0 ]; then
  echo "boundary check ok: interop confined to Core/GameAPI, no net types inside it"
fi
exit $fail
