# 4GE sandbox bots. Adapted from GalaAI/ark apps/desktop-node/aethernet/Dockerfile
# (origin/main): same fixed-runtime idea, but the bots drive the sandbox's
# AEthernet Chromium over CDP, so they need playwright-core only and no
# bundled browser. Dependencies are installed at image build time because the
# bots run on the sandbox's internal network with no egress.
FROM node:24-bookworm-slim
WORKDIR /bots
RUN npm init -y >/dev/null \
    && npm install --omit=dev --no-audit --no-fund --no-update-notifier playwright-core@1.64.1 \
    && npm cache clean --force
COPY bot.mjs ./
USER 1000:1000
ENTRYPOINT ["node", "/bots/bot.mjs"]
