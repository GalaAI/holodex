# 4GE sandbox browser (Eden AEthernet lite).
#
# Copied from GalaAI/ark infra/local/eden-desktop/Dockerfile (origin/main), the
# "desktop" stage plus its "aethernet" target, and narrowed for 4GE:
#   - kept: Debian Chromium on Xvfb and openbox, socat for the app's loopback
#     origin and the CDP relay, x11vnc, tini, the non-root eden user (uid 1000),
#     the CDP healthcheck, and the AEthernet managed policy that turns off the
#     browser's password, card and address stores.
#   - dropped: the kernel, kmod, sshd, ffmpeg and python guest helpers (they
#     serve the Eden micro-OS VM, not a browser sandbox) and the CDP pipe relay.
#   - added: noVNC (websockify) so the builder can watch the bots live on the
#     sandbox viewer port.
FROM debian:bookworm-slim AS aethernet
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates chromium curl fonts-liberation fonts-noto-color-emoji openbox procps socat \
    tini x11vnc xdotool xvfb novnc websockify \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --uid 1000 eden \
    && mkdir /profile && chown eden:eden /profile \
    && mkdir -p /etc/chromium/policies/managed
# AEthernet guests never keep passwords, payment cards or addresses.
COPY policy.json /etc/chromium/policies/managed/aethernet.json
COPY entrypoint.sh /usr/local/bin/forge-aethernet
RUN sed -i 's/\r$//' /usr/local/bin/forge-aethernet \
    && chmod 755 /usr/local/bin/forge-aethernet \
    && chmod 644 /etc/chromium/policies/managed/aethernet.json \
    && usermod -p '*' eden
USER 1000:1000
ENV DISPLAY=:99 HOME=/home/eden
EXPOSE 9223 6080
HEALTHCHECK --interval=3s --timeout=2s --start-period=20s --retries=20 \
    CMD curl -fsS http://127.0.0.1:9222/json/version >/dev/null && xdotool getdisplaygeometry >/dev/null || exit 1
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/forge-aethernet"]
