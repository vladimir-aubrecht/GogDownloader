#!/bin/sh
set -eu
umask 077
mkdir -p "$XDG_CONFIG_HOME" "$XDG_DATA_HOME"
umask "${ARCHIVER_UMASK:-022}"
exec dotnet /app/GameArchiver.Cli.dll "$@"
