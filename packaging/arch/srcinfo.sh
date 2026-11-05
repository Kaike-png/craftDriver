#!/usr/bin/env bash
# Gera o .SRCINFO do AUR a partir do PKGBUILD com "makepkg --printsrcinfo" — o PKGBUILD é a única fonte
# (antes o .SRCINFO era escrito à mão no build.sh e podia divergir dele).
# Uso: packaging/arch/srcinfo.sh <pasta com o PKGBUILD>
# Sem makepkg (Debian/Ubuntu, CI) usa um contêiner archlinux. O makepkg recusa rodar como root e exige pastas
# graváveis, então o contêiner roda com o uid de quem chamou numa cópia do PKGBUILD em /tmp.
# SRCINFO_DOCKER=1 força o contêiner (reproduz o CI numa máquina com makepkg).
set -euo pipefail
dir="$(cd "$1" && pwd)"
tmp="$dir/.SRCINFO.tmp"
trap 'rm -f "$tmp"' EXIT
if [ "${SRCINFO_DOCKER:-0}" != 1 ] && command -v makepkg >/dev/null && [ "$(id -u)" != 0 ]; then
  (cd "$dir" && makepkg --printsrcinfo) > "$tmp"
else
  uid="$(id -u)"; gid="$(id -g)"
  [ "$uid" = 0 ] && { uid=65534; gid=65534; } # root no host: usa "nobody" no contêiner
  docker run --rm -u "$uid:$gid" -e HOME=/tmp -v "$dir/PKGBUILD:/in/PKGBUILD:ro" archlinux:latest \
    sh -c 'mkdir -p /tmp/b && cp /in/PKGBUILD /tmp/b/ && cd /tmp/b && makepkg --printsrcinfo' > "$tmp"
fi
grep -q '^pkgbase = ' "$tmp" || { echo "srcinfo.sh: saída inesperada do makepkg" >&2; exit 1; }
mv "$tmp" "$dir/.SRCINFO"
