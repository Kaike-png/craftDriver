#!/usr/bin/env bash
# Instala os pacotes de dist/ em contêineres limpos e roda smoke.sh.
# Uso: packaging/test/run.sh [ubuntu|debian|fedora ...]   (padrão: todos)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="${OUT:-$ROOT/dist/test}"; mkdir -p "$OUT"
DEB=$(cd "$ROOT/dist" && ls craftdriver_*_amd64.deb); RPM=$(cd "$ROOT/dist" && ls craftdriver-*.x86_64.rpm)
TOOLS_DEB="xvfb x11-utils imagemagick fonts-dejavu-core fonts-noto-color-emoji"
TOOLS_RPM="xorg-x11-server-Xvfb xwininfo ImageMagick dejavu-sans-fonts google-noto-color-emoji-fonts"
run() {
  local distro=$1 image=$2 install=$3
  echo "######## $distro ($image)"
  docker run --rm --device /dev/fuse --cap-add SYS_ADMIN --security-opt apparmor:unconfined \
    -e DISTRO="$distro" -v "$ROOT/dist:/dist:ro" -v "$ROOT/packaging/test:/t:ro" -v "$OUT:/out" "$image" \
    sh -c "$install && sh /t/smoke.sh" 2>&1 | tee "$OUT/$distro.log" | grep -vE "^(Get:|Hit:|Selecting|Preparing|Unpacking|Setting up|Processing|Reading|Building|Fetched|  [a-z]|Last metadata|Downloading|Installing|Running|Verifying|Upgrading|Cleanup|\[|\(|Importing|Complete|Dependencies|Transaction|Package |Total)" | tail -40
}
want="${*:-ubuntu debian fedora arch}"
for d in $want; do case $d in
  ubuntu) run ubuntu ubuntu:24.04 "export DEBIAN_FRONTEND=noninteractive; apt-get update -qq && apt-get install -y -qq /dist/$DEB $TOOLS_DEB >/dev/null" ;;
  debian) run debian debian:12 "export DEBIAN_FRONTEND=noninteractive; apt-get update -qq && apt-get install -y -qq /dist/$DEB $TOOLS_DEB >/dev/null" ;;
  fedora) run fedora fedora:latest "dnf install -y -q /dist/$RPM $TOOLS_RPM >/dev/null" ;;
  # Arch: compila o PKGBUILD do AUR com makepkg (tarball local no lugar do download) e instala com pacman
  arch) run arch archlinux:latest "pacman -Syu --noconfirm -q base-devel xorg-server-xvfb xorg-xwininfo imagemagick ttf-dejavu noto-fonts-emoji >/dev/null \
          && useradd -m builder && mkdir /b && cp /dist/aur/PKGBUILD /dist/craftdriver-*-linux-x64.tar.gz /b/ && chown -R builder /b \
          && su builder -c 'cd /b && makepkg --nodeps --noconfirm >/dev/null' && pacman -U --noconfirm /b/craftdriver-bin-*.pkg.tar.zst >/dev/null" ;;
esac; done
