#!/bin/zsh
# The relay's keys and guards, end to end: a test build of the relay (swiftc -D RELAY_TEST) in a fresh folder on port
# 47572, the installed Uselim and its folder left as they are. ./Tools/test-relay.sh prints a line per check and stops
# with exit 1 at the first one that fails.
set -e
cd "${0:A:h}/.."
port=47572
dir=$(mktemp -d -t uselim-relay-test)
bin=build.noindex/relay-test
mkdir -p build.noindex
swiftc -D RELAY_TEST -target x86_64-apple-macos13.0 Sources/*.swift -o $bin
[[ -x $bin ]] || { echo "the test build failed"; exit 1 }

pid=0
start() {
  USELIM_TEST_RELAY=$dir USELIM_TEST_PORT=$port $bin >> $dir/out.log 2>&1 &
  pid=$!
  for i in {1..50}; do
    curl -s -o /dev/null "http://127.0.0.1:$port/" && return 0
    sleep 0.1
  done
  echo "the test relay failed to start"
  exit 1
}
stop() {
  kill $pid 2>/dev/null || true
  wait $pid 2>/dev/null || true
}
trap 'stop; rm -rf $dir' EXIT

passed=0
# check <what> <status wanted> <curl arguments...>
check() {
  local what=$1 want=$2
  shift 2
  local got=$(curl -s -o $dir/body -w '%{http_code}' --max-time 5 "$@")
  if [[ $got == $want ]]; then
    passed=$((passed + 1))
    echo "ok    $what ($got)"
  else
    echo "FAIL  $what: $got, wanted $want"
    cat $dir/body
    echo
    exit 1
  fi
}
expect() {
  local what=$1
  shift
  if "$@"; then
    passed=$((passed + 1))
    echo "ok    $what"
  else
    echo "FAIL  $what"
    exit 1
  fi
}

start
url="http://127.0.0.1:$port"
key=$(<$dir/local.key)
code=$(/usr/bin/sed -E 's/.*"code":"([0-9]{6})".*/\1/' $dir/pairing.json)
other() { printf '%06d' $(( (10#$code + $1) % 1000000 )) }
mac=(-H 'X-Uselim: 1')
# from the phone through Tailscale Serve: fromphone <what> <status wanted> <address> <code> <curl arguments...>
fromphone() {
  local what=$1 want=$2 address=$3 given=$4
  shift 4
  check "$what" $want -H 'X-Uselim: 1' -H 'Host: mac.example.ts.net' -H "X-Forwarded-For: $address" \
    -H "X-Uselim-Key: $given" "$@"
}
session=test-session-0001
json=(-H 'Content-Type: application/json')

# the Mac's own callers: the local key opens /api and /relay; an older mod's wait passes for the ask held that moment
expect "the local key is 64 hex characters, readable by this user alone" \
  test ${#key} -eq 64 -a "$(stat -f %Lp $dir/local.key)" = 600
check "the header alone reads nothing from the Mac" 403 "${mac[@]}" $url/api/state
check "a wrong local key reads nothing" 403 "${mac[@]}" -H "X-Uselim-Key: $(other 1)" $url/api/state
check "the local key reads the state" 200 "${mac[@]}" -H "X-Uselim-Key: $key" $url/api/state
fromphone "the phone pairs with its code" 200 100.64.0.1 $code $url/api/state
fromphone "the phone's answer waits for the ask" 200 100.64.0.1 $code "${json[@]}" \
  -d "{\"session\":\"$session\",\"ask\":\"q1\",\"allow\":true}" $url/api/answer
check "a wait on the header alone, for an ask held nowhere, is refused" 403 "${mac[@]}" \
  "$url/relay/wait?session=$session&ask=q1"
print -r -- '{"id":"q1","kind":"permission"}' > $dir/asks/$session.json
check "an older mod's wait for the ask held that moment passes" 200 "${mac[@]}" "$url/relay/wait?session=$session&ask=q1"
expect "and takes the phone's answer" /usr/bin/grep -q '"answered"' $dir/body
: > $dir/asks/$session.json
fromphone "the phone's next answer waits too" 200 100.64.0.1 $code "${json[@]}" \
  -d "{\"session\":\"$session\",\"ask\":\"q2\",\"allow\":false}" $url/api/answer
check "a wait with the local key passes" 200 "${mac[@]}" -H "X-Uselim-Key: $key" "$url/relay/wait?session=$session&ask=q2"
fromphone "the waits stay with the Mac" 403 100.64.0.1 $code "$url/relay/wait?session=$session&ask=q2"
fromphone "the local key from the tailnet counts as a wrong code" 401 100.64.0.1 $key $url/api/state

# a paired device's own count: the fifth distinct wrong code pauses it, the pause outlasting a restart
fromphone "a second device pairs" 200 100.64.0.2 $code $url/api/state
for i in {1..4}; do
  fromphone "wrong code $i from the second paired device" 401 100.64.0.2 $(other $((i + 30))) $url/api/state
done
fromphone "the fifth pauses that device alone" 429 100.64.0.2 $(other 35) $url/api/state
fromphone "the phone carries on meanwhile" 200 100.64.0.1 $code $url/api/state
stop
start
fromphone "the paired device's pause outlasts a restart" 429 100.64.0.2 $code $url/api/state

# the right code leaves a paired address's count as it stands: the phone's polls hand out zero fresh tries
fromphone "a fourth device pairs" 200 100.64.0.4 $code $url/api/state
for i in {1..4}; do
  fromphone "wrong code $i under the fourth device" 401 100.64.0.4 $(other $((i + 60))) $url/api/state
done
fromphone "the right code under it still passes" 200 100.64.0.4 $code $url/api/state
fromphone "one more wrong code makes five, and pauses it" 429 100.64.0.4 $(other 65) $url/api/state
fromphone "the right code under it waits out the pause" 429 100.64.0.4 $code $url/api/state

# the guard across devices: addresses a program on the Mac makes up share one count
fromphone "a third device pairs" 200 100.64.0.3 $code $url/api/state
for i in {1..9}; do
  fromphone "wrong code $i from a made-up address" 401 100.64.1.$i $(other $((i + 10))) $url/api/state
done
fromphone "the tenth wrong code across unpaired devices pauses them all" 429 100.64.1.10 $(other 20) $url/api/state
fromphone "a fresh address with the right code waits out the pause" 429 100.64.1.11 $code $url/api/state
fromphone "a blank code from a made-up address answers 401" 401 100.64.1.21 "" $url/api/state
fromphone "a blank code from the paired phone answers the same" 401 100.64.0.1 "" $url/api/state
fromphone "a wrong code from the paired phone answers like a stranger's" 429 100.64.0.1 $(other 40) $url/api/state
for i in {1..5}; do
  fromphone "wrong code $i under a paired address in the pause answers alike, and counts" 429 100.64.0.3 \
    $(other $((i + 50))) $url/api/state
done
fromphone "after five, the right code under that address waits too" 429 100.64.0.3 $code $url/api/state
fromphone "the paired phone carries on through the pause" 200 100.64.0.1 $code $url/api/state
expect "the guard keeps tags, the codes themselves off the disk" \
  sh -c "! /usr/bin/grep -q -e '$(other 11)' -e '$(other 31)' $dir/guard.json"
stop
start
fromphone "the pause holds across a restart" 429 100.64.1.12 $code $url/api/state
fromphone "the paired phone carries on after a restart" 200 100.64.0.1 $code $url/api/state

# notifications: Apple's push service alone, from the phone, under the code it paired with
subscription() { print -r -- "{\"subscription\":{\"endpoint\":\"$1\",\"keys\":{\"p256dh\":\"BAAA\",\"auth\":\"AAAA\"}}}" }
fromphone "an endpoint outside Apple's push service is refused" 400 100.64.0.1 $code "${json[@]}" \
  -d "$(subscription https://fcm.googleapis.com/fcm/send/test)" $url/api/push
fromphone "a plain http endpoint is refused" 400 100.64.0.1 $code "${json[@]}" \
  -d "$(subscription http://web.push.apple.com/test)" $url/api/push
fromphone "an Apple push endpoint is taken" 200 100.64.0.1 $code "${json[@]}" \
  -d "$(subscription https://web.push.apple.com/QTest)" $url/api/push
check "a subscription from the Mac itself is refused" 403 "${mac[@]}" -H "X-Uselim-Key: $key" "${json[@]}" \
  -d "$(subscription https://web.push.apple.com/QMac)" $url/api/push
expect "the subscription carries its code's tag" /usr/bin/grep -q '"tag"' $dir/push.json
stop
print -r -- "{\"code\":\"$(other 500000)\"}" > $dir/pairing.json
start
expect "a new pairing code leaves the old code's subscriptions behind" \
  test "$(/usr/bin/grep -o '"endpoint"' $dir/push.json | wc -l | tr -d ' ')" = 0
stop
print -r -- '[{"endpoint":"https://web.push.apple.com/QBefore","keys":{"p256dh":"BAAA","auth":"AAAA"}},{"endpoint":"https://push.example.com/x","keys":{"p256dh":"BAAA","auth":"AAAA"}}]' > $dir/push.json
start
expect "a subscription from before the tags joins the current code, and one outside Apple's leaves" \
  sh -c "/usr/bin/grep -q QBefore $dir/push.json && ! /usr/bin/grep -q push.example.com $dir/push.json && /usr/bin/grep -q '\"tag\"' $dir/push.json"

echo "$passed checks passed"
