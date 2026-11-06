#!/usr/bin/env bash
# Publica sem single-file, com trimming (mesmo formato dos pacotes) e instala em ~/.local, sem sudo.
# Uso: ./install.sh [--uninstall]. Tarball pronto: ./install-user.sh (não precisa de SDK).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
if [[ "${1:-}" == --uninstall ]]; then exec bash "$HERE/packaging/install-user.sh" --uninstall; fi
[[ $# == 0 ]] || { echo "Uso: $0 [--uninstall]" >&2; exit 2; }
DOTNET="$(command -v dotnet || echo "${HOME}/.dotnet/dotnet")"
[[ -x "$DOTNET" ]] || { echo "dotnet SDK 10 não encontrado" >&2; exit 1; }
command -v fusermount3 >/dev/null || { echo "Instale o pacote fuse3 (fusermount3)" >&2; exit 1; }
case "$(uname -m)" in x86_64) RID=linux-x64;; aarch64) RID=linux-arm64;; *) echo "Arquitetura não suportada" >&2; exit 1;; esac
STAGE=$(mktemp -d); trap 'rm -rf "$STAGE"' EXIT
rm -rf "$HERE"/src/*/obj/Release/net10.0/"$RID"
echo "→ Publicando ($RID, self-contained, trimming)…"
"$DOTNET" publish "$HERE/src/CraftDriver.App/CraftDriver.App.csproj" -c Release -r "$RID" --self-contained \
  -warnaserror -p:PublishSingleFile=false -p:PublishTrimmed=true -p:DebugType=none -o "$STAGE/lib"
mv "$STAGE/lib/CraftDriver.App" "$STAGE/lib/craftdriver"
cp -r "$HERE/packaging/share" "$STAGE/share"
install -Dm644 "$HERE/src/CraftDriver.App/Assets/app-256.png" "$STAGE/share/icons/hicolor/256x256/apps/craftdriver.png"
install -Dm644 "$HERE/extensions/nautilus/craftdriver.py" "$STAGE/share/nautilus-python/extensions/craftdriver.py"
install -Dm644 -t "$STAGE/share/file-manager/actions" "$HERE"/extensions/pcmanfm/craftdriver-*.desktop
install -Dm644 "$HERE/extensions/thunar/uca.xml" "$STAGE/share/craftdriver/thunar/uca.xml"
install -Dm755 "$HERE/extensions/thunar/install-uca.py" "$STAGE/share/craftdriver/thunar/install-uca.py"
cp "$HERE/packaging/install-user.sh" "$STAGE/"
bash "$STAGE/install-user.sh"
