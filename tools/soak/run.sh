#!/bin/bash
# Soak of vs-runtime on this PC: two simulated cameras, a simulated Modbus PLC (camera channels)
# at a fixed part rate, a per-minute record of the runtime's resources. tools/soak/README.md.
#
# run.sh <workDir> <imagesFolder> [seconds=1800] [rate=225] [budgetMs=100] [crop=1920x1200]
#
# Takes the heavy-test lock (AGENTS.md rule 13) for the whole run, builds Release, plays the
# images (split between the cameras, every other file), and leaves in <workDir>/out:
# samples.csv (per minute), plc.csv (per part), results.csv (contract stream), ref-cam*.txt
# (fresh-reader verdicts), report.txt and runtime.log. Each frame is the centre crop of an image
# at the line camera's size (crop "full" plays the images as they are, through symbolic links);
# the crops stay in <workDir>, never in the repository (customer data).
# Each frame comes as late as the line camera sends it: the job's exposure plus the frame over
# the link, GigE unless SOAK_LINK=USB3 (RT-SF-01).
set -u
W=$(mkdir -p "$1" && cd "$1" && pwd)
IMAGES=$2
SECS=${3:-1800}
RATE=${4:-225}
BUDGET=${5:-100}
CROP=${6:-1920x1200}
REPO=$(cd "$(dirname "$0")/../.." && pwd)
export PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH DOTNET_ROOT=$HOME/.dotnet
SOCK=${SOAK_SOCKET:-/tmp/vs-soak-$$.sock}   # short: a Unix socket path has a 104-byte limit on macOS
PORT=${SOAK_PLC_PORT:-15530}
COUNTERS=${DOTNET_COUNTERS:-dotnet-counters}
LOCK=/tmp/vs-heavy-test.lock
OUT=$W/out
pids=()

cleanup() {
  for p in "${pids[@]}"; do kill "$p" 2>/dev/null; done
  [ -n "${RT:-}" ] && kill "$RT" 2>/dev/null && wait "$RT" 2>/dev/null
  rm -f "$SOCK"
  [ "$(cat $LOCK/owner 2>/dev/null | cut -d' ' -f1)" = "soak-$$" ] && rm -rf "$LOCK"
}
trap cleanup EXIT INT TERM

until mkdir "$LOCK" 2>/dev/null; do
  [ -n "$(find "$LOCK" -maxdepth 0 -mmin +45)" ] && rm -rf "$LOCK"
  sleep 60
done
echo "soak-$$ runtime soak ${SECS}s $(date)" > "$LOCK/owner"
echo "lock taken $(date), load $(sysctl -n vm.loadavg)"

# the engine first: the .NET builds copy its libraries next to vs-runtime only when they exist
B="$REPO/engine/build"
[ -f "$B/build.ninja" ] || cmake -S "$REPO/engine" -B "$B" -G Ninja -DCMAKE_BUILD_TYPE=Release > "$W/build.log" 2>&1 || { tail -20 "$W/build.log"; exit 1; }
cmake --build "$B" --target codereader vs_ocr vs_locate >> "$W/build.log" 2>&1 || { tail -20 "$W/build.log"; exit 1; }
# vs-runtime builds with the remote service agent from the submodule (an older checkout may not have it yet)
git -C "$REPO" submodule update --init --recursive >> "$W/build.log" 2>&1 || { tail -20 "$W/build.log"; exit 1; }
dotnet build -c Release -v q --nologo "$REPO/dotnet/Runtime.Host/Runtime.Host.csproj" >> "$W/build.log" 2>&1 || { tail -20 "$W/build.log"; exit 1; }
dotnet build -c Release -v q --nologo "$REPO/tools/soak/ctl/ctl.csproj" -o "$W/ctl" >> "$W/build.log" 2>&1 || { tail -20 "$W/build.log"; exit 1; }
cp "$B"/libcodereader.dylib "$B"/libvs_ocr.dylib "$B"/libvs_locate.dylib "$W/ctl/" 2>/dev/null
VSRT="$REPO/dotnet/Runtime.Host/bin/Release/net10.0/vs-runtime"
[ -x "$VSRT" ] || VSRT="dotnet $REPO/dotnet/Runtime.Host/bin/Release/net10.0/vs-runtime.dll"
CTL="dotnet $W/ctl/soakctl.dll"

# fresh data, jobs and camera folders each run
rm -rf "$W/data" "$W/jobs" "$W/cam1" "$W/cam2" "$OUT"
mkdir -p "$W/data" "$W/jobs" "$W/cam1" "$W/cam2" "$OUT"
i=0
while IFS= read -r f; do
  case "$(file -b "$f")" in PC\ bitmap*|PNG\ image*) ;; *) continue ;; esac
  # numbered names: the runtime and analyze.py then agree on the play order
  dst="$W/cam$((i % 2 + 1))/$(printf %03d $i).bmp"
  if [ "$CROP" = full ]; then ln -s "$f" "$dst"; else $CTL crop "$f" "$dst" ${CROP%x*} ${CROP#*x} || exit 1; fi
  i=$((i + 1))
done < <(find "$IMAGES" -maxdepth 1 -type f \( -iname '*.bmp' -o -iname '*.png' \) | LC_ALL=C sort)
echo "frames: cam1 $(ls "$W/cam1" | wc -l | tr -d ' '), cam2 $(ls "$W/cam2" | wc -l | tr -d ' ')"
echo "{\"Protocol\":\"ModbusTcp\",\"Port\":$PORT,\"WatchdogMs\":1000,\"PollMs\":2}" > "$W/data/plc.json"
ARGS=(--Studio:JobsFolder="$W/jobs" --Studio:DataFolder="$W/data" --Studio:ModelsFolder="$REPO/ml/models" --Studio:LicenceRequired=false)
$VSRT add-user soak "Soak" Operator --pin 2468 "${ARGS[@]}" > /dev/null
$CTL mkjob "$W/jobs" "$W/cam1" "$W/cam2" "$BUDGET"

$VSRT --console "${ARGS[@]}" --Runtime:Endpoint="$SOCK" --Runtime:Source=camera \
  --Runtime:SimulatorFolder="$W/cam1" --Runtime:SimulatorLink="${SOAK_LINK:-GigE}" > "$OUT/runtime.log" 2>&1 &
RT=$!
for _ in $(seq 60); do [ -S "$SOCK" ] && break; sleep 1; done
[ -S "$SOCK" ] || { echo "runtime did not start"; tail -20 "$OUT/runtime.log"; exit 1; }
RTPID=$(pgrep -P $RT vs-runtime || echo $RT)
$CTL start "$SOCK" soak 2468 SOAK2CAM || exit 1
$CTL watch "$SOCK" soak 2468 "$OUT/results.csv" &
pids+=($!)
$COUNTERS collect -p "$RTPID" --refresh-interval 10 --format csv -o "$OUT/counters.csv" --counters System.Runtime > /dev/null 2>&1 &
pids+=($!)
python3 "$REPO/tools/soak/sample.py" "$RTPID" "$W/data" "$OUT/plc.csv" "$OUT/counters.csv" "$OUT/samples.csv" $CTL status "$SOCK" &
SAMPLER=$!
pids+=($SAMPLER)
# SOAK_VMMAP="5 15 25": the runtime's memory by region (vmmap -summary) at those minutes; it pauses
# the process for about a second, so a part in that minute can be late. Live malloc bytes
# (ALLOCATED) tell a leak from freed memory the allocator keeps.
if [ -n "${SOAK_VMMAP:-}" ]; then
  ( t0=$(date +%s); for m in $SOAK_VMMAP; do
      while [ $(( $(date +%s) - t0 )) -lt $((m * 60 + 30)) ]; do sleep 5; done
      vmmap -summary "$RTPID" > "$OUT/vmmap-$m.txt" 2>&1
    done ) &
  pids+=($!)
fi
sleep 1
echo "soak ${SECS}s at ${RATE}/min, runtime pid $RTPID, load before $(sysctl -n vm.loadavg)"
python3 "$REPO/tools/soak/plcsim.py" "$PORT" "$RATE" "$SECS" "$OUT/plc.csv" 20 2 | tee "$OUT/plc.txt"
echo "load after $(sysctl -n vm.loadavg)"
# the sampler started 1 s before the PLC, so its last full minute is written; stop it before
# the PLC link's absence shows as an alarm
touch "$OUT/samples.csv.stop"
wait $SAMPLER 2>/dev/null
[ -n "${SOAK_VMMAP:-}" ] && { vmmap -summary "$RTPID" > "$OUT/vmmap-end.txt" 2>&1; leaks "$RTPID" > "$OUT/leaks.txt" 2>&1; grep "total leaked" "$OUT/leaks.txt"; }
$CTL counters "$SOCK" soak 2468 | tee "$OUT/counters.txt"
$CTL status "$SOCK" | tee -a "$OUT/counters.txt"
$CTL stop "$SOCK" soak 2468

# reference verdicts: a fresh reader per camera on the same frames, still under the lock
$CTL local "$W/jobs" SOAK2CAM "$REPO/ml/models" 0 "$W/cam1" > "$OUT/ref-cam1.txt"
$CTL local "$W/jobs" SOAK2CAM "$REPO/ml/models" 1 "$W/cam2" > "$OUT/ref-cam2.txt"
{
  python3 "$REPO/tools/soak/analyze.py" "$W/data/results.db" "$W/cam1" "$OUT/ref-cam1.txt" "$W/cam2" "$OUT/ref-cam2.txt"
  python3 "$REPO/tools/soak/report.py" "$OUT/samples.csv"
} | tee "$OUT/report.txt"
