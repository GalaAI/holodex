#!/bin/sh
# 4GE sandbox seed: copy the build's app folder into the sandbox volume and
# prepare it. Runs once per sandbox start, as root, on the only network with
# egress (package installs need the registry). The app service then runs the
# copy as uid 1000 on the internal network; the host folder is never mounted
# into the running app.
set -eu
if [ ! -d /src ] || [ -z "$(ls -A /src 2>/dev/null)" ]; then
    echo "The build's app folder is empty, so there is nothing to put in the sandbox." >&2
    exit 2
fi
find /workspace -mindepth 1 -maxdepth 1 -exec rm -rf {} +
# Host node_modules may hold another platform's native binaries; reinstall instead.
(cd /src && tar -cf - --exclude=./node_modules --exclude=./.git .) | tar -xf - -C /workspace
cd /workspace
if [ -f package.json ]; then
    plan=$(node /sandbox/package-plan.mjs /workspace/package.json)
    export npm_config_update_notifier=false npm_config_fund=false npm_config_audit=false
    case "$plan" in *deps*)
        echo "seed: installing dependencies"
        if [ -f pnpm-lock.yaml ]; then corepack enable >/dev/null 2>&1 && pnpm install --frozen-lockfile=false
        elif [ -f yarn.lock ]; then corepack enable >/dev/null 2>&1 && yarn install
        elif [ -f package-lock.json ]; then npm ci
        else npm install
        fi
        ;;
    esac
    case "$plan" in *build*)
        echo "seed: running the build script"
        npm run build
        ;;
    esac
fi
chown -R 1000:1000 /workspace
echo "seed: ready ($(find /workspace -type f -not -path '*/node_modules/*' | wc -l) files)"
