#!/bin/sh
# Roda dentro do contêiner, como root, após a instalação do pacote.
set -e
echo "== arquivos"
ls -l /usr/bin/craftdriver /usr/lib/craftdriver/craftdriver /usr/share/applications/craftdriver.desktop \
      /usr/share/kio/servicemenus/craftdriver.desktop /usr/share/icons/hicolor/256x256/apps/craftdriver.png | awk '{print $1, $NF}'
echo "== versão"
craftdriver --version
useradd -m tester 2>/dev/null || adduser -D tester
H=/home/tester/h; mkdir -p $H/config
echo "{\"MountPoint\":\"$H/OneDrive\",\"CacheDir\":\"$H/cache\"}" > $H/config/config.json
chown -R tester $H
echo "== headless + FUSE"
su tester -c "XDG_RUNTIME_DIR=/tmp/rt CRAFTDRIVER_HOME=$H sh -c 'mkdir -p /tmp/rt; craftdriver --headless > /tmp/hl.log 2>&1 &'"
sleep 6
su tester -c "XDG_RUNTIME_DIR=/tmp/rt CRAFTDRIVER_HOME=$H craftdriver status"
grep craftdriver /proc/mounts | awk '{print "montado:", $1, $2, $3}'
su tester -c "XDG_RUNTIME_DIR=/tmp/rt CRAFTDRIVER_HOME=$H craftdriver quit"
sleep 3
grep -c craftdriver /proc/mounts || echo "desmontado"
echo "== interface gráfica (Xvfb)"
# a janela precisa aparecer: sem xwininfo não há como conferir, então é falha (antes passava calado)
# casa título ("craftDriver") ou WM_CLASS ("craftdriver" "CraftDriver.App"), sem diferenciar maiúsculas: logo ao
# abrir, o título X11 ainda pode ser "Window" (visto no Xvfb) e a classe já casa
WINDOW_MATCH="${WINDOW_MATCH:-craftDriver}"   # trocável só para provar que a falha funciona
WINDOW_TIMEOUT="${WINDOW_TIMEOUT:-30}"
command -v xwininfo >/dev/null || { echo "FALHA: xwininfo ausente (x11-utils / xwininfo / xorg-xwininfo)"; exit 1; }
Xvfb :9 -screen 0 1280x1024x24 >/dev/null 2>&1 &
sleep 2
su tester -c "DISPLAY=:9 XDG_RUNTIME_DIR=/tmp/rt CRAFTDRIVER_HOME=$H sh -c 'craftdriver > /tmp/gui.log 2>&1 &'"
win=""; i=0
while [ $i -lt "$WINDOW_TIMEOUT" ]; do
  win=$(DISPLAY=:9 xwininfo -root -tree 2>/dev/null | grep -iF "\"$WINDOW_MATCH\"" | head -2 || true)
  [ -n "$win" ] && break
  i=$((i + 1)); sleep 1
done
sleep 2 # deixa a janela terminar de desenhar antes da captura
DISPLAY=:9 import -window root /out/gui-$DISTRO.png 2>/dev/null || true
if [ -z "$win" ]; then
  echo "FALHA: a janela \"$WINDOW_MATCH\" não apareceu em ${WINDOW_TIMEOUT} s"
  echo "--- gui.log"; cat /tmp/gui.log
  exit 1
fi
echo "janela (após ${i} s):"; echo "$win"
su tester -c "XDG_RUNTIME_DIR=/tmp/rt CRAFTDRIVER_HOME=$H craftdriver quit" || { echo "--- gui.log"; cat /tmp/gui.log; exit 1; }
echo "OK $DISTRO"
