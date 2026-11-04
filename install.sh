#!/usr/bin/env bash
# Publica o OneDrive Linux em ~/.local/opt/craftdriver e cria o comando ~/.local/bin/craftdriver.
# Uso: ./install.sh            (instala/atualiza)
#      ./install.sh --uninstall
set -euo pipefail

DEST="${HOME}/.local/opt/craftdriver"
BIN="${HOME}/.local/bin/craftdriver"
HERE="$(cd "$(dirname "$0")" && pwd)"

if [[ "${1:-}" == "--uninstall" ]]; then
  "$BIN" quit 2>/dev/null || true
  rm -rf "$DEST" "$BIN" \
    "${HOME}/.local/share/applications/craftdriver.desktop" \
    "${HOME}/.config/autostart/craftdriver.desktop" \
    "${HOME}/.local/share/kio/servicemenus/craftdriver.desktop" \
    "${HOME}/.local/share/icons/hicolor/256x256/apps/craftdriver.png"
  rm -f "${HOME}/.local/share/nautilus/scripts/craftDriver - "* "${HOME}/.local/share/nemo/scripts/craftDriver - "* 2>/dev/null || true
  echo "Removido. Configuração (~/.config/craftdriver), estado e cache foram mantidos."
  exit 0
fi

DOTNET="$(command -v dotnet || echo "${HOME}/.dotnet/dotnet")"
[[ -x "$DOTNET" ]] || { echo "dotnet SDK 10 não encontrado"; exit 1; }
command -v fusermount3 >/dev/null || { echo "Instale o pacote fuse3 (fusermount3)"; exit 1; }

ARCH="$(uname -m)"; case "$ARCH" in x86_64) RID=linux-x64;; aarch64) RID=linux-arm64;; *) echo "Arquitetura $ARCH não suportada"; exit 1;; esac

echo "→ Publicando ($RID, self-contained)…"
"$DOTNET" publish "$HERE/src/CraftDriver.App/CraftDriver.App.csproj" -c Release -r "$RID" --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "$DEST.new" >/dev/null

"$BIN" quit 2>/dev/null && sleep 2 || true
rm -rf "$DEST"
mv "$DEST.new" "$DEST"
mkdir -p "$(dirname "$BIN")"
ln -sf "$DEST/CraftDriver.App" "$BIN"

echo "→ Instalado em $DEST"
echo "  Comando: $BIN  (garanta que ~/.local/bin está no PATH)"
echo "  Abra o app e use Configurações ▸ 'Instalar atalho no menu e itens no menu de contexto'."
