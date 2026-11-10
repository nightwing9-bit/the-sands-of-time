#!/usr/bin/env bash
# Source this file (do not execute it) to use a .NET SDK that satisfies global.json:
#   source scripts/checks/dotnet-env.sh
# global.json requires SDK 10.0.402 or newer (rollForward latestFeature). The system `dotnet` on some machines is older
# (10.0.401) and is refused with "The command could not be loaded". A newer SDK installed with
#   curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version 10.0.403 --install-dir ~/.dotnet-10.0.403
# is picked up from DOTNET_SDK_DIR (default ~/.dotnet-10.0.403). global.json is never edited or bypassed.
DOTNET_SDK_DIR="${DOTNET_SDK_DIR:-$HOME/.dotnet-10.0.403}"
if [ ! -x "$DOTNET_SDK_DIR/dotnet" ]; then
  echo "dotnet-env: no SDK at $DOTNET_SDK_DIR (set DOTNET_SDK_DIR or install 10.0.402+)" >&2
  return 1 2>/dev/null || exit 1
fi
export DOTNET_ROOT="$DOTNET_SDK_DIR"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
echo "dotnet-env: $(command -v dotnet) $(dotnet --version)"
