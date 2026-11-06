#!/usr/bin/env bash
# Instala/desinstala o tarball sem SDK e sem sudo; configuração, estado e cache são preservados.
# Uso: ./install-user.sh [--uninstall]
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
DEST="${HOME}/.local/opt/craftdriver"
BIN="${HOME}/.local/bin/craftdriver"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
CONFIG="${XDG_CONFIG_HOME:-$HOME/.config}"

if [[ "${1:-}" == --uninstall ]]; then
  "$BIN" quit 2>/dev/null || true
  if command -v systemctl >/dev/null; then
    systemctl --user disable --now craftdriver.service 2>/dev/null || true
  fi
  rm -f "$BIN" "$DATA/applications/craftdriver.desktop" "$CONFIG/autostart/craftdriver.desktop" \
    "$CONFIG/systemd/user/craftdriver.service" "$DATA/kio/servicemenus/craftdriver.desktop" \
    "$DATA/icons/hicolor/256x256/apps/craftdriver.png" "$DATA/icons/hicolor/scalable/apps/craftdriver.svg" \
    "$DATA/icons/hicolor/symbolic/apps/craftdriver-symbolic.svg" "$DATA/nautilus-python/extensions/craftdriver.py" \
    "$DATA/metainfo/io.github.kaike_png.craftdriver.metainfo.xml"
  rm -f "$DATA/nautilus/scripts/craftDriver - "* "$DATA/nemo/scripts/craftDriver - "* \
    "$DATA/file-manager/actions/craftdriver-"*.desktop
  rm -rf "$DEST" "$DATA/craftdriver/thunar" "$HOME/.net/CraftDriver.App" "$HOME/.net/OneDriveLinux.App"
  if command -v systemctl >/dev/null; then systemctl --user daemon-reload 2>/dev/null || true; fi
  echo "Removido. Configuração, estado e cache do craftDriver foram mantidos."
  exit 0
fi
[[ $# == 0 ]] || { echo "Uso: $0 [--uninstall]" >&2; exit 2; }
[[ -x "$HERE/lib/craftdriver" && -d "$HERE/share" ]] || { echo "Extraia o tarball inteiro antes de instalar." >&2; exit 1; }
# Copiar antes de parar/substituir permite executar este script a partir da instalação existente.
STAGE="$(mktemp -d "${DEST}.XXXXXXXX" 2>/dev/null || { mkdir -p "$(dirname "$DEST")"; mktemp -d "${DEST}.XXXXXXXX"; })"
trap 'rm -rf "$STAGE"' EXIT
cp -a "$HERE/lib" "$HERE/share" "$STAGE/"
cp "$HERE/install-user.sh" "$STAGE/"
for f in LICENSE README.md CHANGELOG.md; do [[ ! -f "$HERE/$f" ]] || cp "$HERE/$f" "$STAGE/"; done
"$BIN" quit 2>/dev/null || true
rm -rf "$DEST"
mv "$STAGE" "$DEST"
mkdir -p "$(dirname "$BIN")" "$DATA"
ln -sfn "$DEST/lib/craftdriver" "$BIN"
# Copia somente arquivos deste app; nunca substitui a pasta share inteira do usuário.
cp -a "$DEST/share/." "$DATA/"
# Exec aponta para o caminho absoluto do modo usuário (não exige ~/.local/bin no PATH da sessão).
# Escapes de desktop: barra, aspas, crase, dólar e % (código de campo). Nenhuma expansão pelo shell.
exec_path=${BIN//\\/\\\\}; exec_path=${exec_path//\"/\\\"}; exec_path=${exec_path//\`/\\\`}; exec_path=${exec_path//\$/\\\$}; exec_path=${exec_path//%/%%}
for f in "$DATA/applications/craftdriver.desktop" "$DATA/kio/servicemenus/craftdriver.desktop"; do
  content=$(<"$f")
  content=${content//Exec=craftdriver/Exec=\"$exec_path\"}
  printf '%s\n' "$content" > "$f"
done
chmod 755 "$DATA/kio/servicemenus/craftdriver.desktop"
# A extensão do Nautilus substitui os scripts legados (o Nemo continua usando seus scripts).
rm -f "$DATA/nautilus/scripts/craftDriver - "*
rm -rf "$HOME/.net/CraftDriver.App" "$HOME/.net/OneDriveLinux.App"
command -v update-desktop-database >/dev/null && update-desktop-database "$DATA/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -f -t "$DATA/icons/hicolor" >/dev/null 2>&1 || true
echo "Instalado em $DEST. Comando: $BIN"
echo "Nautilus: instale nautilus-python e reinicie o gerenciador para carregar emblemas e menus."
