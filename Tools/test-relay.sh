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
  USELIM_TEST_RELAY=$dir USELIM_TEST_PORT=$port USELIM_TEST_IDLE=2 USELIM_TEST_CONNECTIONS=4 USELIM_TEST_BODY=3 \
    USELIM_TEST_PAIR_SECONDS=3 USELIM_TEST_LOGIN=me@github $bin >> $dir/out.log 2>&1 &
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

# the lasting code a phone paired with before the phones' own keys
legacy=246810
print -r -- "{\"code\":\"$legacy\"}" > $dir/pairing.json
start
url="http://127.0.0.1:$port"
key=$(<$dir/local.key)
other() { printf '%06d' $(( (123456 + $1) % 1000000 )) }
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

# pairing: Pair a phone's six digits pair one phone within their minutes (3 s in this test build, ten in Uselim),
# trading for that phone's own key, which the Mac keeps as its hash beside the phone's name from its browser; the
# lasting code from before opens the relay through its days
iphone=(-A 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148')
digits() { check "$1" 200 "${mac[@]}" -H "X-Uselim-Key: $key" -X POST $url/relay/test-pair
  digits=$(/usr/bin/plutil -extract code raw -o - $dir/body) }
listed() { /usr/bin/plutil -extract phones.$1.$2 raw -o - $dir/phones.json 2>/dev/null }
digits "Pair a phone shows six digits"
fromphone "a phone pairs with them, trading them for its own key" 200 100.64.0.1 $digits "${iphone[@]}" \
  -X POST $url/api/pair
code=$(/usr/bin/plutil -extract token raw -o - $dir/body)
expect "its key is 43 characters, and the Mac keeps its hash alone" \
  sh -c "test ${#code} -eq 43 && ! /usr/bin/grep -q -e '$code' $dir/phones.json && test \"\$(stat -f %Lp $dir/phones.json)\" = 600"
expect "the list names it iPhone, from its browser" test "$(listed 0 name)" = iPhone
fromphone "its key opens the relay" 200 100.64.0.1 $code $url/api/state
fromphone "the digits pair one phone alone" 401 100.64.0.1 $digits "${iphone[@]}" -X POST $url/api/pair
expect "and the phone hears they are spent" /usr/bin/grep -q '"spent"' $dir/body
fromphone "the lasting code from before opens the relay through its days" 200 100.64.0.6 $legacy $url/api/state
digits "Pair a phone shows fresh digits for the next"
fromphone "the digits open nothing past the pairing call" 401 100.64.0.6 $digits $url/api/state
sleep 3.5
fromphone "past their minutes they pair nothing" 401 100.64.0.1 $digits -X POST $url/api/pair
expect "and the phone hears they are spent too" /usr/bin/grep -q '"spent"' $dir/body
fromphone "a request naming another tailnet login is refused" 403 100.64.0.1 $code \
  -H 'Tailscale-User-Login: other@github' $url/api/state
fromphone "one naming the Mac's own login passes" 200 100.64.0.1 $code -H 'Tailscale-User-Login: Me@github' \
  $url/api/state
check "a wait on the header alone, for an ask held nowhere, is refused" 403 "${mac[@]}" \
  "$url/relay/wait?session=$session&ask=q1"
fromphone "an answer for an ask held nowhere is refused" 409 100.64.0.1 $code "${json[@]}" \
  -d "{\"session\":\"$session\",\"ask\":\"q1\",\"allow\":true}" $url/api/answer
print -r -- '{"id":"q1","kind":"permission"}' > $dir/asks/$session.json
fromphone "the phone's answer for the ask it shows waits for its wait" 200 100.64.0.1 $code "${json[@]}" \
  -d "{\"session\":\"$session\",\"ask\":\"q1\",\"allow\":true}" $url/api/answer
check "an older mod's wait for the ask held that moment passes" 200 "${mac[@]}" "$url/relay/wait?session=$session&ask=q1"
expect "and takes the phone's answer" /usr/bin/grep -q '"answered"' $dir/body
print -r -- '{"id":"q2","kind":"permission"}' > $dir/asks/$session.json
fromphone "an answer for the ask before it, now moved on, is refused" 409 100.64.0.1 $code "${json[@]}" \
  -d "{\"session\":\"$session\",\"ask\":\"q1\",\"allow\":true}" $url/api/answer
fromphone "the phone's next answer waits too" 200 100.64.0.1 $code "${json[@]}" \
  -d "{\"session\":\"$session\",\"ask\":\"q2\",\"allow\":false}" $url/api/answer
: > $dir/asks/$session.json
check "a wait with the local key passes" 200 "${mac[@]}" -H "X-Uselim-Key: $key" "$url/relay/wait?session=$session&ask=q2"
check "a Host of another tailnet name is refused" 403 -H 'X-Uselim: 1' -H 'Host: other.example.ts.net' \
  -H 'X-Forwarded-For: 100.64.0.1' -H "X-Uselim-Key: $code" $url/api/state
fromphone "an Origin of another tailnet name is refused" 403 100.64.0.1 $code -H 'Origin: https://other.example.ts.net' \
  $url/api/state
fromphone "the Mac's own name as the Origin passes" 200 100.64.0.1 $code -H 'Origin: https://mac.example.ts.net' \
  $url/api/state
fromphone "the waits stay with the Mac" 403 100.64.0.1 $code "$url/relay/wait?session=$session&ask=q2"
fromphone "the local key from the tailnet counts as a wrong code" 401 100.64.0.1 $key $url/api/state

# a paired device's own count: the fifth distinct wrong code pauses it, the pause outlasting a restart
fromphone "a second address with the phone's key passes" 200 100.64.0.2 $code $url/api/state
for i in {1..4}; do
  fromphone "wrong code $i from the second paired device" 401 100.64.0.2 $(other $((i + 30))) $url/api/state
done
fromphone "the fifth pauses that device alone" 429 100.64.0.2 $(other 35) $url/api/state
fromphone "the phone carries on meanwhile" 200 100.64.0.1 $code $url/api/state
stop
start
fromphone "the paired device's pause outlasts a restart" 429 100.64.0.2 $code $url/api/state

# the phone's key leaves a paired address's count as it stands: the phone's polls hand out zero fresh tries
fromphone "a fourth address with the phone's key passes" 200 100.64.0.4 $code $url/api/state
for i in {1..4}; do
  fromphone "wrong code $i under the fourth device" 401 100.64.0.4 $(other $((i + 60))) $url/api/state
done
fromphone "the phone's key under it still passes" 200 100.64.0.4 $code $url/api/state
fromphone "one more wrong code makes five, and pauses it" 429 100.64.0.4 $(other 65) $url/api/state
fromphone "the phone's key under it waits out the pause" 429 100.64.0.4 $code $url/api/state

# the guard across devices: addresses a program on the Mac makes up share one count
fromphone "a third address with the phone's key passes" 200 100.64.0.3 $code $url/api/state
for i in {1..9}; do
  fromphone "wrong code $i from a made-up address" 401 100.64.1.$i $(other $((i + 10))) $url/api/state
done
fromphone "the tenth wrong code across unpaired devices pauses them all" 429 100.64.1.10 $(other 20) $url/api/state
fromphone "a fresh address with the phone's key waits out the pause" 429 100.64.1.11 $code $url/api/state
fromphone "a blank code from a made-up address answers 401" 401 100.64.1.21 "" $url/api/state
fromphone "a blank code from the paired phone answers the same" 401 100.64.0.1 "" $url/api/state
fromphone "a wrong code from the paired phone answers like a stranger's" 429 100.64.0.1 $(other 40) $url/api/state
for i in {1..5}; do
  fromphone "wrong code $i under a paired address in the pause answers alike, and counts" 429 100.64.0.3 \
    $(other $((i + 50))) $url/api/state
done
fromphone "after five, the phone's key under that address waits too" 429 100.64.0.3 $code $url/api/state
fromphone "the paired phone carries on through the pause" 200 100.64.0.1 $code $url/api/state
expect "the guard keeps tags, the codes themselves off the disk" \
  sh -c "! /usr/bin/grep -q -e '$(other 11)' -e '$(other 31)' $dir/guard.json"
stop
start
fromphone "the pause holds across a restart" 429 100.64.1.12 $code $url/api/state
fromphone "the paired phone carries on after a restart" 200 100.64.0.1 $code $url/api/state

# notifications: Apple's push service alone, from the phone, under the phone's own tag; Remove ends a phone's key and
# its notifications, an untagged subscription from before joins the lasting code, and the lasting code trades once
subscription() { print -r -- "{\"subscription\":{\"endpoint\":\"$1\",\"keys\":{\"p256dh\":\"BAAA\",\"auth\":\"AAAA\"}}}" }
# a refusal of any kind: 401, or 429 while the pause across unpaired devices lasts
refused() {
  local got=$(curl -s -o $dir/body -w '%{http_code}' --max-time 5 -H 'X-Uselim: 1' -H 'Host: mac.example.ts.net' \
    -H "X-Forwarded-For: $2" -H "X-Uselim-Key: $3" "${@:4}")
  expect "$1 ($got)" test $got = 401 -o $got = 429
}
fromphone "an endpoint outside Apple's push service is refused" 400 100.64.0.1 $code "${json[@]}" \
  -d "$(subscription https://fcm.googleapis.com/fcm/send/test)" $url/api/push
fromphone "a plain http endpoint is refused" 400 100.64.0.1 $code "${json[@]}" \
  -d "$(subscription http://web.push.apple.com/test)" $url/api/push
fromphone "an Apple push endpoint is taken" 200 100.64.0.1 $code "${json[@]}" \
  -d "$(subscription https://web.push.apple.com/QTest)" $url/api/push
check "a subscription from the Mac itself is refused" 403 "${mac[@]}" -H "X-Uselim-Key: $key" "${json[@]}" \
  -d "$(subscription https://web.push.apple.com/QMac)" $url/api/push
expect "the subscription carries its phone's tag" /usr/bin/grep -q "\"tag\":\"$(listed 0 id)\"" $dir/push.json
check "Remove ends a phone" 200 "${mac[@]}" -H "X-Uselim-Key: $key" -X POST "$url/relay/test-remove?id=$(listed 0 id)"
expect "and its notifications" test "$(/usr/bin/grep -o '"endpoint"' $dir/push.json | wc -l | tr -d ' ')" = 0
refused "a removed phone's key opens nothing" 100.64.0.6 $code $url/api/state
digits "Pair a phone shows fresh digits once more"
fromphone "the phone pairs again with them" 200 100.64.0.1 $digits "${iphone[@]}" -X POST $url/api/pair
code=$(/usr/bin/plutil -extract token raw -o - $dir/body)
expect "and takes its name back" test "$(listed 0 name)" = iPhone
stop
print -r -- '[{"endpoint":"https://web.push.apple.com/QBefore","keys":{"p256dh":"BAAA","auth":"AAAA"}},{"endpoint":"https://push.example.com/x","keys":{"p256dh":"BAAA","auth":"AAAA"}}]' > $dir/push.json
start
expect "a subscription from before the tags joins the lasting code, and one outside Apple's leaves" \
  sh -c "/usr/bin/grep -q QBefore $dir/push.json && ! /usr/bin/grep -q push.example.com $dir/push.json && /usr/bin/grep -q '\"tag\"' $dir/push.json"
fromphone "the lasting code trades for its phone's own key" 200 100.64.0.6 $legacy "${iphone[@]}" -X POST $url/api/pair
traded=$(/usr/bin/plutil -extract token raw -o - $dir/body)
expect "the list names that phone iPhone 2" test "$(listed 1 name)" = "iPhone 2"
expect "the lasting code retires" test ! -e $dir/pairing.json
expect "its notifications move to that phone" /usr/bin/grep -q "\"tag\":\"$(listed 1 id)\"" $dir/push.json
fromphone "the traded key opens the relay" 200 100.64.0.6 $traded $url/api/state
refused "the lasting code opens nothing after" 100.64.0.6 $legacy $url/api/state

# a permission's card carries the call in full, so Allow on the phone answers for what it reads: an edit's path and
# both sides, a long file's first 100,000 characters with the rest counted; the notice names the first line, and the
# phone's answer comes back as the hook's decision
asks=$dir/asks/hook-session-01.json
hook() {
  print -r -- "{\"session_id\":\"hook-session-01\",\"hook_event_name\":\"PermissionRequest\",\"cwd\":\"/test/demo\",\"tool_name\":\"$1\",\"tool_input\":$2}" |
    USELIM_TEST_RELAY=$dir USELIM_TEST_PORT=$port $bin --permission > $dir/decision.json &
}
held() { for i in {1..50}; do [[ -s $asks ]] && return 0; sleep 0.1; done; return 1 }
field() { /usr/bin/plutil -extract $1 raw -o - $asks 2>/dev/null }
noticed() {
  for i in {1..40}; do
    curl -s -o $dir/notices -H 'X-Uselim: 1' -H 'Host: mac.example.ts.net' -H 'X-Forwarded-For: 100.64.0.1' \
      -H "X-Uselim-Key: $code" $url/api/notices
    [[ $(/usr/bin/plutil -extract notices.0.body raw -o - $dir/notices 2>/dev/null) == $1 ]] && return 0
    sleep 0.1
  done
  return 1
}
hook Edit '{"file_path":"/test/demo/a.txt","old_string":"one\ntwo","new_string":"one\nthree"}'
edit=$!
expect "the hook puts a permission card up" held
expect "an edit's card holds its path and both sides, line by line" \
  test "$(field detail)" = $'/test/demo/a.txt\n\n- one\n- two\n+ one\n+ three'
expect "the notice names the card's first line" noticed "Edit: /test/demo/a.txt"
fromphone "the phone allows the edit" 200 100.64.0.1 $code "${json[@]}" \
  -d "{\"session\":\"hook-session-01\",\"ask\":\"$(field id)\",\"allow\":true}" $url/api/answer
wait $edit || true
expect "the hook prints the phone's allow as its decision" /usr/bin/grep -q '"behavior":"allow"' $dir/decision.json
expect "and takes its card down" test ! -s $asks
hook Write "{\"file_path\":\"/test/demo/b.txt\",\"content\":\"$(head -c 100010 /dev/zero | tr '\0' x)\"}"
write=$!
expect "a long file's card holds its first 100,000 characters" held
expect "and counts the rest" test "$(field more)" = 28
kill $write
wait $write || true
expect "a hook cancelled by the Mac takes its card down" test ! -s $asks

# the server checks a request at its head: an upload refused there ends before any of its body is read, and an
# admitted one streams to a spool file and lands whole, sent at once or in chunks. A connection silent for the idle
# time closes (2 s in this test build, 30 s in Uselim), and past the connection limit (4 here, 24 in Uselim) a new one
# hears 503
print -r -- "{\"session\":\"$session\",\"cwd\":\"/test/demo\",\"project\":\"demo\",\"status\":\"idle\"}" > $dir/chats/$session.json
head -c 3000000 /dev/urandom > $dir/upload.bin
head -c 1000 /dev/zero > $dir/small.bin
fromphone "a blank code ends a 100 MB upload at its head" 401 100.64.0.1 "" -H 'Content-Length: 104857600' \
  --data-binary @$dir/small.bin "$url/api/upload?session=$session&name=big.bin"
fromphone "an upload past 200 MiB stops at its head" 413 100.64.0.1 $code -H 'Content-Length: 314572800' \
  --data-binary @$dir/small.bin "$url/api/upload?session=$session&name=big.bin"
fromphone "a 3 MB upload streams in" 200 100.64.0.1 $code --data-binary @$dir/upload.bin \
  "$url/api/upload?session=$session&name=upload.bin"
expect "and lands whole" cmp -s $dir/upload.bin $dir/files/$session/upload.bin
fromphone "an upload sent in chunks streams in" 200 100.64.0.1 $code -H 'Transfer-Encoding: chunked' \
  --data-binary @$dir/upload.bin "$url/api/upload?session=$session&name=chunked.bin"
expect "and lands whole too" cmp -s $dir/upload.bin $dir/files/$session/chunked.bin
expect "the spool holds nothing after" test -z "$(ls -A $dir/spool)"
zmodload zsh/net/tcp zsh/datetime
ztcp 127.0.0.1 $port
quiet=$REPLY
print -n -u $quiet $'GET / HTTP/1.1\r\n'
began=$EPOCHREALTIME
read -t 8 -u $quiet line || true
(( waited = EPOCHREALTIME - began ))
ztcp -c $quiet
expect "a connection silent for the idle time closes" test $(( waited > 1.5 && waited < 4 )) -eq 1
held=()
for i in {1..4}; do
  ztcp 127.0.0.1 $port
  print -n -u $REPLY $'GET / HTTP/1.1\r\n'
  held+=($REPLY)
done
check "past the connection limit a new connection hears 503" 503 "${mac[@]}" -H "X-Uselim-Key: $key" $url/api/state
for fd in $held; do ztcp -c $fd; done
sleep 0.3
check "a freed connection takes the next request" 200 "${mac[@]}" -H "X-Uselim-Key: $key" $url/api/state

# a body in chunks as an admitted caller might send it: a size past every limit, a size line past 1 KiB in one piece,
# and a trickle past the body's deadline (3 s in this test build, 30 s in Uselim, an upload's ten minutes)
raw() {
  ztcp 127.0.0.1 $port
  local fd=$REPLY
  print -n -u $fd $'POST /api/answer HTTP/1.1\r\nHost: 127.0.0.1:'$port$'\r\nX-Uselim: 1\r\nX-Uselim-Key: '$key$'\r\nTransfer-Encoding: chunked\r\n\r\n'"$1"
  answer=''
  read -t 4 -u $fd answer || true
  ztcp -c $fd
  answer=${answer%$'\r'}
}
raw $'1\r\na\r\n7fffffffffffffff\r\n'
expect "a chunk size past every limit answers 413" test "$answer" = "HTTP/1.1 413 Content Too Large"
check "and the relay carries on" 200 "${mac[@]}" -H "X-Uselim-Key: $key" $url/api/state
raw "1;${(l:2000::x:)}"$'\r\na\r\n0\r\n\r\n'
expect "a chunk line past 1 KiB answers 400" test "$answer" = "HTTP/1.1 400 Bad Request"
print -n 'abcdefghij' > $dir/ten.bin
began=$EPOCHREALTIME
curl -s -o /dev/null --max-time 8 --limit-rate 1 "${mac[@]}" -H "X-Uselim-Key: $key" "${json[@]}" \
  --data-binary @$dir/ten.bin $url/api/answer || true
(( waited = EPOCHREALTIME - began ))
expect "a body trickling past its deadline closes at the deadline" test $(( waited > 2.5 && waited < 6 )) -eq 1

# a notebook edit's card names its kind first, so a cell's deletion reads as one
hook NotebookEdit '{"notebook_path":"/test/demo/n.ipynb","cell_id":"c7","edit_mode":"delete","new_source":""}'
cell=$!
expect "a notebook cell's deletion holds its card" held
expect "and reads as one" test "$(field detail)" = $'/test/demo/n.ipynb\n\ndelete cell c7'
kill $cell
wait $cell || true

# a body admitted at its head, its phone removed before the rest arrives: the route answers 401 and the file stays out
digits "Pair a phone shows digits for one more phone"
fromphone "one more phone pairs" 200 100.64.0.1 $digits "${iphone[@]}" -X POST $url/api/pair
racer=$(/usr/bin/plutil -extract token raw -o - $dir/body)
racerid=$(listed 2 id)
ztcp 127.0.0.1 $port
race=$REPLY
print -n -u $race $'POST /api/upload?session='$session$'&name=race.bin HTTP/1.1\r\nHost: mac.example.ts.net\r\nX-Uselim: 1\r\nX-Forwarded-For: 100.64.0.1\r\nX-Uselim-Key: '$racer$'\r\nContent-Length: 1000\r\n\r\n'"${(l:100::x:)}"
sleep 0.3
check "Remove ends that phone while its upload arrives" 200 "${mac[@]}" -H "X-Uselim-Key: $key" -X POST \
  "$url/relay/test-remove?id=$racerid"
print -n -u $race "${(l:900::x:)}"
answer=''
read -t 4 -u $race answer || true
ztcp -c $race
expect "the upload's route answers 401" test "${answer%$'\r'}" = "HTTP/1.1 401 Unauthorized"
expect "and its file stays out" test ! -e $dir/files/$session/race.bin

# the lasting code's days ending while Uselim runs: it retires within a tick, its notifications with it
stop
print -r -- "{\"code\":\"135790\",\"until\":$(( ${EPOCHREALTIME%.*} * 1000 + 5000 ))}" > $dir/pairing.json
start
fromphone "a lasting code within its days subscribes" 200 100.64.0.6 135790 "${json[@]}" \
  -d "$(subscription https://web.push.apple.com/QLapse)" $url/api/push
sleep 6
expect "past its days it retires while Uselim runs" test ! -e $dir/pairing.json
expect "its notifications with it" sh -c "! /usr/bin/grep -q QLapse $dir/push.json"
refused "and it opens nothing" 100.64.0.6 135790 $url/api/state

echo "$passed checks passed"
