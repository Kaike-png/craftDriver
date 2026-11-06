#!/usr/bin/env bash
# Instala/desinstala o tarball sem SDK e sem sudo; configuração, estado e cache são preservados.
# Uso: ./install-user.sh [--uninstall]
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
DEST="${HOME}/.local/opt/craftdriver"
BIN="${HOME}/.local/bin/craftdriver"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
CONFIG="${XDG_CONFIG_HOME:-$HOME/.config}"
UCA="$CONFIG/Thunar/uca.xml"
QUIT_TIMEOUT="${CRAFTDRIVER_QUIT_TIMEOUT:-30}"

# Trava de instância única (mesmo caminho que AppPaths.LockFile): a nova instância só sobe quando ela está livre.
if [[ -n "${CRAFTDRIVER_HOME:-}" ]]; then LOCK="$CRAFTDRIVER_HOME/run/craftdriver.lock"
else
  tmp="${TMPDIR:-/tmp}"
  LOCK="${XDG_RUNTIME_DIR:-${tmp%/}/craftdriver-$(id -un)}/craftdriver.lock"
fi

# Encerra a instância em execução e ESPERA ela sair (desmontar e soltar a trava) antes de substituir os arquivos:
# senão o usuário abre o app logo depois, a nova instância acha a trava ainda presa, conclui "já em execução" e sai —
# e a antiga termina em seguida: nenhuma fica. Versões novas de 'craftdriver quit' já esperam; as antigas (que podem
# ser justamente as instaladas) voltam na hora, por isso a espera aqui também. Devolve 1 se o prazo estourar.
stop_running() {
  local pids=() p exe
  # processos desta instalação, anotados ANTES do quit (depois do rm -rf o /proc/PID/exe fica "(deleted)")
  for p in /proc/[0-9]*; do
    exe=$(readlink "$p/exe" 2>/dev/null) || continue
    [[ "${exe% (deleted)}" == "$DEST/lib/craftdriver" ]] && pids+=("${p#/proc/}")
  done
  [[ -e "$BIN" ]] && { "$BIN" quit >/dev/null 2>&1 || true; }
  local deadline=$((SECONDS + QUIT_TIMEOUT))
  for p in "${pids[@]}"; do
    while kill -0 "$p" 2>/dev/null; do
      ((SECONDS < deadline)) || return 1
      sleep 0.2
    done
  done
  # a trava cobre também uma instância de outra instalação (pacote) com o mesmo perfil
  if [[ -e "$LOCK" ]] && command -v flock >/dev/null; then
    local left=$((deadline - SECONDS)); ((left > 0)) || left=1
    flock -w "$left" "$LOCK" true 2>/dev/null || return 1
  fi
  return 0
}

if [[ "${1:-}" == --uninstall ]]; then
  stop_running || echo "Aviso: o craftDriver não terminou em ${QUIT_TIMEOUT} s; removendo assim mesmo." >&2
  # ações do Thunar ANTES de apagar a pasta que contém o script (senão ficam órfãs apontando para $BIN)
  if [[ -f "$UCA" ]] && grep -q 'craftdriver-' "$UCA" && command -v python3 >/dev/null \
     && [[ -f "$DATA/craftdriver/thunar/install-uca.py" ]]; then
    python3 "$DATA/craftdriver/thunar/install-uca.py" --remove --uca "$UCA" >/dev/null || true
  fi
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
if ! stop_running; then
  echo "O craftDriver em execução não terminou em ${QUIT_TIMEOUT} s (pasta em uso?). Feche-o e rode de novo." >&2
  exit 1
fi
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
# PCManFM: TryExec é um caminho (sem aspas; escapes de string do desktop) e Exec segue as regras acima
tryexec_path=${BIN//\\/\\\\}
for f in "$DATA/file-manager/actions/craftdriver-"*.desktop; do
  [[ -f "$f" ]] || continue
  content=$(<"$f")
  content=${content//TryExec=craftdriver/TryExec=$tryexec_path}
  content=${content//Exec=craftdriver /Exec=\"$exec_path\" }
  printf '%s\n' "$content" > "$f"
done
# Thunar: atualiza as ações (caminho absoluto) se o usuário usa Thunar ou já as instalou. O comando do uca.xml
# passa pelo g_shell_parse_argv: aspas simples protegem espaços no caminho.
if command -v python3 >/dev/null && { command -v thunar >/dev/null || { [[ -f "$UCA" ]] && grep -q 'craftdriver-' "$UCA"; }; }; then
  python3 "$DATA/craftdriver/thunar/install-uca.py" --uca "$UCA" --command "'${BIN//\'/\'\\\'\'}'" >/dev/null || true
fi
# A extensão do Nautilus substitui os scripts legados (o Nemo continua usando seus scripts).
rm -f "$DATA/nautilus/scripts/craftDriver - "*
rm -rf "$HOME/.net/CraftDriver.App" "$HOME/.net/OneDriveLinux.App"
command -v update-desktop-database >/dev/null && update-desktop-database "$DATA/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -f -t "$DATA/icons/hicolor" >/dev/null 2>&1 || true
echo "Instalado em $DEST. Comando: $BIN"
echo "Nautilus: instale nautilus-python e reinicie o gerenciador para carregar emblemas e menus."
