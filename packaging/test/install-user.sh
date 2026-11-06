#!/usr/bin/env bash
# Dentro do contêiner, sem SDK: instala o tarball em HOME descartável, exercita o app e desinstala sem perder dados.
set -euo pipefail
case "$(uname -m)" in x86_64) ARCH=x64;; aarch64) ARCH=arm64;; *) exit 2;; esac
T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
export HOME="$T/usuário com espaço" CRAFTDRIVER_HOME="$T/instancia" DBUS_SESSION_BUS_ADDRESS=unix:path=/nonexistent
export XDG_CONFIG_HOME="$HOME/.config" XDG_DATA_HOME="$HOME/.local/share" XDG_RUNTIME_DIR="$T/run"
mkdir -p "$HOME" "$XDG_CONFIG_HOME/craftdriver" "$HOME/.net/CraftDriver.App" "$XDG_CONFIG_HOME/systemd/user" "$XDG_RUNTIME_DIR"
echo manter > "$XDG_CONFIG_HOME/craftdriver/dados-preservados"
touch "$XDG_CONFIG_HOME/systemd/user/craftdriver.service"
tar -xzf /dist/craftdriver-*-linux-"$ARCH".tar.gz -C "$T"
cd "$T"/craftdriver-*-linux-"$ARCH"
bash install-user.sh
"$HOME/.local/bin/craftdriver" --version
test -f "$HOME/.local/opt/craftdriver/lib/CraftDriver.App.dll"
test ! -d "$HOME/.net/CraftDriver.App"
for icon in scalable/apps/craftdriver.svg symbolic/apps/craftdriver-symbolic.svg 256x256/apps/craftdriver.png; do
  test -f "$XDG_DATA_HOME/icons/hicolor/$icon"
done
test -x "$XDG_DATA_HOME/kio/servicemenus/craftdriver.desktop"
bash install-user.sh --uninstall
test ! -e "$HOME/.local/bin/craftdriver"
test ! -e "$XDG_DATA_HOME/icons/hicolor/scalable/apps/craftdriver.svg"
test ! -e "$XDG_CONFIG_HOME/systemd/user/craftdriver.service"
test "$(cat "$XDG_CONFIG_HOME/craftdriver/dados-preservados")" = manter
echo "Tarball: instalar/desinstalar sem SDK, com espaços no HOME e dados preservados OK"
