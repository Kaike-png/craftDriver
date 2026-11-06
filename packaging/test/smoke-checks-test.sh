#!/bin/sh
# §25.15 — a conferência de desmontagem do smoke.sh precisa FALHAR quando a montagem continua presente.
# Roda as linhas reais do smoke.sh trocando /proc/mounts por um arquivo falso. Sai 1 se algum caso passar calado.
HERE=$(cd "$(dirname "$0")" && pwd)
T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
echo "craftdriver /home/tester/h/OneDrive fuse.onedrive rw,nosuid,nodev 0 0" > "$T/montado"
: > "$T/vazio"
falhas=0
roda() { # $1 = linha do smoke.sh, $2 = arquivo no lugar de /proc/mounts → status do sh -e
  printf 'set -e\n%s\necho CHEGOU-AO-FIM\n' "$(printf '%s' "$1" | sed "s|/proc/mounts|$2|g")" > "$T/s.sh"
  sh "$T/s.sh" > "$T/out" 2>&1
}

desm=$(grep -n 'desmontado' "$HERE/smoke.sh" | head -1 | cut -d: -f2-)
roda "$desm" "$T/montado"
if grep -q CHEGOU-AO-FIM "$T/out"; then
  echo "FALHA: desmontagem: com a montagem presente o smoke segue (saída: $(tr '\n' ' ' < "$T/out"))"; falhas=1
else echo "ok: desmontagem pega montagem presente"; fi

mont=$(grep -n 'montado:' "$HERE/smoke.sh" | head -1 | cut -d: -f2-)
roda "$mont" "$T/vazio"
if grep -q CHEGOU-AO-FIM "$T/out"; then
  echo "FALHA: montagem: sem montagem nenhuma o smoke segue (pipeline termina no awk)"; falhas=1
else echo "ok: montagem ausente para o smoke"; fi

# a correção sugerida no PENDENCIAS ('! grep -q') NÃO para o script: set -e ignora comandos negados com '!'
roda '! grep -q craftdriver /proc/mounts' "$T/montado"
grep -q CHEGOU-AO-FIM "$T/out" && echo "nota: '! grep -q' sob set -e também passa calado (POSIX: status negado não aciona -e)"
exit $falhas
