#!/usr/bin/env bash
# Executado DENTRO da distro KF6: compila/testa, instala em DESTDIR e gera pacote nativo com dependências reais.
# Uso (no host): docker run ... fedora:41 bash packaging/dolphin/build-native.sh fedora41
# Montar o repositório em /repo e executar nele; saída: dist/dolphin/<distro>/.
set -euo pipefail
DISTRO=${1:?informe debian13, ubuntu2504 ou fedora41}
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
VERSION=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$ROOT/src/CraftDriver.App/CraftDriver.App.csproj")
OUT="$ROOT/dist/dolphin/$DISTRO"; mkdir -p "$OUT"
BUILD="/tmp/craftdriver-dolphin-build"
STAGE="/tmp/craftdriver-dolphin-stage"
rm -rf "$BUILD" "$STAGE"
if command -v apt-get >/dev/null; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq --no-install-recommends build-essential cmake ninja-build extra-cmake-modules \
    libkf6kio-dev libkf6coreaddons-dev qt6-base-dev libgl-dev libvulkan-dev dpkg-dev
  PACKAGE=deb
else
  dnf install -y -q --setopt=install_weak_deps=False gcc-c++ cmake ninja-build extra-cmake-modules kf6-kio-devel kf6-kcoreaddons-devel qt6-qtbase-devel rpm-build
  PACKAGE=rpm
fi
cmake -S "$ROOT/extensions/dolphin" -B "$BUILD" -G Ninja -DCMAKE_INSTALL_PREFIX=/usr -DBUILD_TESTING=ON -DCMAKE_BUILD_TYPE=Release
cmake --build "$BUILD" --parallel 2
# /tmp em algumas distros não tem user xattr: testes usam o volume do workspace, como o CI Arch.
CRAFTDRIVER_TEST_DIR="$ROOT" QT_QPA_PLATFORM=offscreen ctest --test-dir "$BUILD" --output-on-failure
DESTDIR="$STAGE" cmake --install "$BUILD"
install -Dm644 "$ROOT/LICENSE" "$STAGE/usr/share/licenses/craftdriver-dolphin/LICENSE"
if [[ "$PACKAGE" == deb ]]; then
  mkdir -p "$STAGE/DEBIAN"
  # dpkg-shlibdeps usa as bibliotecas da própria distro, não nomes/ABI inferidos do Arch.
  mkdir -p "$BUILD/debian"; echo 'Source: craftdriver-dolphin' > "$BUILD/debian/control"
  mapfile -t plugins < <(find "$STAGE" -name '*.so')
  DEPS=$(cd "$BUILD" && dpkg-shlibdeps -O "${plugins[@]/#/-e}" | sed -n 's/^shlibs:Depends=//p')
  cat > "$STAGE/DEBIAN/control" <<EOF
Package: craftdriver-dolphin
Version: $VERSION-1
Architecture: $(dpkg --print-architecture)
Maintainer: Kaike de Paula <kaikedepaula8@gmail.com>
Section: kde
Priority: optional
Depends: craftdriver (>= $VERSION), $DEPS
Description: Plugins KF6 do Dolphin para o craftDriver
 Emblemas de status e menu de contexto apenas nas montagens do craftDriver.
EOF
  dpkg-deb --root-owner-group --build "$STAGE" "$OUT/craftdriver-dolphin_${VERSION}-1_${DISTRO}_$(dpkg --print-architecture).deb"
else
  TOP=/tmp/craftdriver-dolphin-rpm
  RELEASE_DATE=$(sed -n "s/^## \\[$VERSION\\] - //p" "$ROOT/CHANGELOG.md")
  RPM_DATE=$(LC_ALL=C date -d "$RELEASE_DATE" '+%a %b %d %Y')
  mkdir -p "$TOP"/{BUILD,BUILDROOT,RPMS,SOURCES,SPECS,SRPMS}
  cat > "$TOP/SPECS/craftdriver-dolphin.spec" <<EOF
Name: craftdriver-dolphin
Version: $VERSION
Release: 1%{?dist}
Summary: Plugins KF6 do Dolphin para o craftDriver
License: MIT
Requires: craftdriver >= $VERSION
%description
Emblemas de status e menu de contexto apenas nas montagens do craftDriver.
%install
cp -a $STAGE/. %{buildroot}/
%files
/usr/lib*/qt6/plugins/kf6/overlayicon/craftdriveroverlayplugin.so
/usr/lib*/qt6/plugins/kf6/kfileitemaction/craftdriveractionplugin.so
%config(noreplace) /etc/xdg/kservicemenurc
%license /usr/share/licenses/craftdriver-dolphin/LICENSE
%changelog
* $RPM_DATE Kaike de Paula <kaikedepaula8@gmail.com> - $VERSION-1
- Compilação do plugin KF6 para esta distribuição.
EOF
  rpmbuild --define "_topdir $TOP" -bb "$TOP/SPECS/craftdriver-dolphin.spec"
  find "$TOP/RPMS" -name 'craftdriver-dolphin-*.rpm' ! -name '*debuginfo*' ! -name '*debugsource*' -exec cp {} "$OUT/" \;
fi
(cd "$OUT" && sha256sum -- *.$PACKAGE > SHA256SUMS)
# Docker roda como root para instalar as dependências; os artefatos voltam ao dono do workspace do host.
chown -R "$(stat -c '%u:%g' "$ROOT")" "$ROOT/dist/dolphin"
echo "Pacote Dolphin pronto: $OUT"
