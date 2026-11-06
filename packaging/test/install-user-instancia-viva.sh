#!/usr/bin/env bash
# §25.3 — install-user.sh com o app ABERTO e --uninstall com as ações do Thunar instaladas.
# Tudo isolado: HOME, CRAFTDRIVER_HOME, XDG_RUNTIME_DIR e D-Bus descartáveis (nunca fala com a instância real).
# Uso: install-user-instancia-viva.sh <pasta do tarball extraído>   (ou /dist/craftdriver-*-linux-ARCH.tar.gz no contêiner)
# Sai 1 se algum dos defeitos aparecer.
set -uo pipefail
SRC="${1:?pasta do tarball extraído}"
T=$(mktemp -d); trap 'kill $(cat "$T"/pids 2>/dev/null) 2>/dev/null; rm -rf "$T"' EXIT
export HOME="$T/home" CRAFTDRIVER_HOME="$T/instancia" DBUS_SESSION_BUS_ADDRESS=unix:path=/nonexistent CRAFTDRIVER_NO_NOTIFY=1
export XDG_CONFIG_HOME="$HOME/.config" XDG_DATA_HOME="$HOME/.local/share" XDG_RUNTIME_DIR="$T/run"
mkdir -p "$HOME" "$CRAFTDRIVER_HOME/config" "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"
echo "{\"MountPoint\":\"$T/OneDrive\",\"CacheDir\":\"$T/cache\"}" > "$CRAFTDRIVER_HOME/config/config.json"
BIN="$HOME/.local/bin/craftdriver"
falhas=0

bash "$SRC/install-user.sh" >/dev/null
"$BIN" --headless > "$T/a.log" 2>&1 & echo $! >> "$T/pids"; A=$!
for _ in $(seq 50); do "$BIN" status >/dev/null 2>&1 && break; sleep 0.2; done
"$BIN" status >/dev/null 2>&1 || { echo "ERRO de arnês: instância A não subiu"; cat "$T/a.log"; exit 2; }

# 1) o quit do instalador volta antes de o processo antigo sair?
"$BIN" quit >/dev/null 2>&1
if kill -0 "$A" 2>/dev/null; then echo "OBS: 'craftdriver quit' voltou com a instância antiga ($A) ainda viva"; fi
wait "$A" 2>/dev/null

# 2) cenário do PENDENCIAS: reinstala com o app aberto e o usuário abre logo em seguida
"$BIN" --headless > "$T/b.log" 2>&1 & echo $! >> "$T/pids"; B=$!
for _ in $(seq 50); do "$BIN" status >/dev/null 2>&1 && break; sleep 0.2; done
bash "$SRC/install-user.sh" >/dev/null &&
  { "$BIN" --headless > "$T/c.log" 2>&1 & echo $! >> "$T/pids"; }   # "o usuário abre logo"
C=$!
sleep 8
if "$BIN" status >/dev/null 2>&1; then echo "ok: uma instância ficou rodando após reinstalar com o app aberto"
else
  echo "FALHA §25.3: nenhuma instância ficou após reinstalar com o app aberto"
  echo "  B vivo? $(kill -0 $B 2>/dev/null && echo sim || echo não); C vivo? $(kill -0 $C 2>/dev/null && echo sim || echo não)"
  echo "  saída da nova: $(tr '\n' ' ' < "$T/c.log")"
  falhas=1
fi
"$BIN" quit >/dev/null 2>&1; sleep 2

# 3) --uninstall deixa as ações do Thunar órfãs?
python3 "$XDG_DATA_HOME/craftdriver/thunar/install-uca.py" --command "$BIN" >/dev/null
grep -q craftdriver "$XDG_CONFIG_HOME/Thunar/uca.xml" || { echo "ERRO de arnês: uca.xml sem ações"; exit 2; }
bash "$SRC/install-user.sh" --uninstall >/dev/null
n=$(grep -c "$BIN" "$XDG_CONFIG_HOME/Thunar/uca.xml")
if [ "$n" -gt 0 ]; then echo "FALHA §25.3: --uninstall deixou $n ações do Thunar apontando para $BIN (apagado)"; falhas=1
else echo "ok: --uninstall removeu as ações do Thunar"; fi
exit $falhas
