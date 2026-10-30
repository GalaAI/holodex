#!/bin/sh
# 4GE sandbox browser entrypoint. Adapted from GalaAI/ark
# infra/local/eden-desktop/entrypoint.sh (origin/main): same Xvfb, openbox,
# socat loopback relay and CDP relay; the Tor (phantmnet) branch and the CDP
# pipe are removed, noVNC is added for the sandbox viewer.
set -eu
export DISPLAY=:99
case "${EDEN_UPSTREAM:-}" in *[!a-zA-Z0-9:._-]*|'') echo 'Expected a bounded app service upstream' >&2; exit 1;; esac
app_port="${EDEN_APP_PORT:-17410}"
case "$app_port" in *[!0-9]*|'') echo 'Expected a numeric app port' >&2; exit 1;; esac
mkdir -p /profile/browser /tmp/runtime-eden
export XDG_RUNTIME_DIR=/tmp/runtime-eden
chmod 700 "$XDG_RUNTIME_DIR"
Xvfb :99 -screen 0 "${EDEN_DISPLAY:-1280x800}x24" -nolisten tcp -ac &
xvfb_pid=$!
for attempt in 1 2 3 4 5 6 7 8 9 10; do
    if xdotool getdisplaygeometry >/dev/null 2>&1; then break; fi
    sleep 0.2
done
openbox >/tmp/openbox.log 2>&1 &
openbox_pid=$!
# The app keeps the same loopback origin inside the guest as on the host
# (http://127.0.0.1:17410), so secure-context APIs behave the same.
socat TCP-LISTEN:"$app_port",bind=127.0.0.1,reuseaddr,fork "TCP:$EDEN_UPSTREAM" &
proxy_pid=$!
# Chromium binds CDP to loopback; this relay is what the bots and the host reach.
socat TCP-LISTEN:9223,reuseaddr,fork TCP:127.0.0.1:9222 &
control_pid=$!
# VNC stays on loopback; only the noVNC web viewer is published (127.0.0.1 on the host).
x11vnc -display :99 -forever -shared -localhost -rfbport 5900 -nopw -quiet >/tmp/vnc.log 2>&1 &
vnc_pid=$!
websockify --web /usr/share/novnc 6080 127.0.0.1:5900 >/tmp/novnc.log 2>&1 &
viewer_pid=$!
# Chromium sandboxing is unavailable in this unprivileged container profile.
# The container boundary is explicit; this is not an independent guest VM.
chromium --no-sandbox --disable-dev-shm-usage --disable-gpu \
    --no-first-run --no-default-browser-check --password-store=basic \
    --disable-features=Translate,MediaRouter --disable-background-networking \
    --user-data-dir=/profile/browser --remote-debugging-port=9222 \
    --window-size="${EDEN_DISPLAY_CSV:-1280,800}" \
    "${EDEN_APP_ROUTE:-http://127.0.0.1:$app_port/}" >/tmp/chromium.log 2>&1 &
browser_pid=$!
cleanup() {
    kill "$browser_pid" "$viewer_pid" "$vnc_pid" "$proxy_pid" "$control_pid" "$openbox_pid" "$xvfb_pid" 2>/dev/null || true
    wait 2>/dev/null || true
}
trap cleanup EXIT
trap 'exit 0' TERM INT
while kill -0 "$browser_pid" && kill -0 "$xvfb_pid" && kill -0 "$proxy_pid" && kill -0 "$control_pid"; do sleep 1; done
exit 1
