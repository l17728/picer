#!/usr/bin/env bash
set -euo pipefail

# Run as root on the IndexService host after copying the published Linux-x64
# service to /opt/picer-index. This script never handles or prints bearer keys.
install -D -m 0644 deploy/systemd/picer-index.service /etc/systemd/system/picer-index.service
install -D -m 0644 deploy/systemd/picer-index-health.service /etc/systemd/system/picer-index-health.service
install -D -m 0644 deploy/systemd/picer-index-health.timer /etc/systemd/system/picer-index-health.timer
install -D -m 0755 deploy/systemd/picer-index-healthcheck /usr/local/libexec/picer-index-healthcheck
install -d -o picer -g picer /var/lib/picer-index /var/log/picer-index
systemctl daemon-reload
systemctl enable --now picer-index.service
systemctl enable --now picer-index-health.timer
systemctl --no-pager --full status picer-index.service
