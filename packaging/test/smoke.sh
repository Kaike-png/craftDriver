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
Xvfb :9 -screen 0 1280x1024x24 >/dev/null 2>&1 &
sleep 2
su tester -c "DISPLAY=:9 XDG_RUNTIME_DIR=/tmp/rt CRAFTDRIVER_HOME=$H sh -c 'craftdriver > /tmp/gui.log 2>&1 &'"
sleep 10
if command -v xwininfo >/dev/null; then DISPLAY=:9 xwininfo -root -tree | grep -i '"craftDriver"' | head -2; fi
DISPLAY=:9 import -window root /out/gui-$DISTRO.png 2>/dev/null || true
su tester -c "XDG_RUNTIME_DIR=/tmp/rt CRAFTDRIVER_HOME=$H craftdriver quit" || { echo "--- gui.log"; cat /tmp/gui.log; exit 1; }
echo "OK $DISTRO"
