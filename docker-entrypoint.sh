#!/bin/bash
set -e

# Regenerate SSH host keys (removed at build time so each container instance gets unique keys)
ssh-keygen -A 2>/dev/null || true

# Set SSH password for appuser — set the SSH_PASSWORD env var to enable password-based login
if [ -n "$SSH_PASSWORD" ]; then
    echo "appuser:$SSH_PASSWORD" | chpasswd
fi

# Install an SSH public key — set SSH_AUTHORIZED_KEY env var for key-based login
if [ -n "$SSH_AUTHORIZED_KEY" ]; then
    install -d -m 700 -o appuser -g appuser /home/appuser/.ssh
    printf '%s\n' "$SSH_AUTHORIZED_KEY" > /home/appuser/.ssh/authorized_keys
    chown appuser:appuser /home/appuser/.ssh/authorized_keys
    chmod 600 /home/appuser/.ssh/authorized_keys
fi

# Start SSH daemon (root required to bind privileged port and manage auth)
/usr/sbin/sshd

# Drop privileges to appuser — gosu exec-replaces the shell so PID 1 is dotnet,
# allowing SIGTERM etc. to reach the app for graceful shutdown.
exec gosu appuser dotnet /app/CloudSelfService.dll
