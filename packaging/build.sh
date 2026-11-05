#!/usr/bin/env bash
# Gera os artefatos de distribuição do craftDriver em dist/ (por arquitetura em ARCHES):
#   craftdriver-<ver>-linux-<x64|arm64>.tar.gz (+ .sha256)   app + arquivos de desktop
#   craftdriver_<ver>-1_<amd64|arm64>.deb                     Debian/Ubuntu
#   craftdriver-<ver>-1.<x86_64|aarch64>.rpm                  Fedora/openSUSE
#   aur/PKGBUILD + .SRCINFO                                   AUR (craftdriver-bin, todas as arquiteturas de ARCHES)
#   aur/craftdriver-dolphin/PKGBUILD + .SRCINFO               AUR (plugin do Dolphin, compilado da fonte)
#
# Formato do app: pasta comum publicada com trimming (/usr/lib/craftdriver/ nos pacotes; lib/ no tarball), sem
# single-file: o single-file extraía as bibliotecas nativas em ~/.net/<app>/<hash>/ (14 MB por versão, nunca
# apagados) e não abria com $HOME só leitura. Medições e motivos: docs/empacotamento.md.
#
# As extensões de gerenciador de arquivos em Python/XML/.desktop (Nautilus, Thunar, PCManFM; ver
# extensions/) vão no tarball e nos pacotes. O plugin C++ do Dolphin (extensions/dolphin) precisa de
# KF6/ECM para compilar e NÃO é gerado aqui: é um pacote à parte (craftdriver-dolphin no AUR, ou
# cmake manual), testado no CI num contêiner archlinux.
#
# Uso: [ARCHES="x64 arm64"] packaging/build.sh [versão]   (padrão: ARCHES=x64; versão do CraftDriver.App.csproj)
# Requer: .NET SDK 10 e Docker (para o nfpm e o makepkg; ou nfpm/makepkg instalados no PATH).
# arm64 é publicado por compilação cruzada (sem emulação); só é testado no CI, num executor ARM.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CSPROJ="$ROOT/src/CraftDriver.App/CraftDriver.App.csproj"
VERSION="${1:-$(grep -oP '(?<=<Version>)[^<]+' "$CSPROJ")}"
export VERSION
ARCHES="${ARCHES:-x64}"
DIST="$ROOT/dist"
DOTNET="$(command -v dotnet || echo "$HOME/.dotnet/dotnet")"

echo "→ craftDriver $VERSION ($ARCHES)"
rm -rf "$DIST"
mkdir -p "$DIST/aur"

run_nfpm() { # $1 = deb|rpm, $2 = arquivo de configuração (relativo a dist/)
  if command -v nfpm >/dev/null; then
    (cd "$DIST" && nfpm package -f "$2" -p "$1" -t .)
  else
    docker run --rm -u "$(id -u):$(id -g)" -v "$DIST:/work" -w /work \
      goreleaser/nfpm package -f "$2" -p "$1" -t . >/dev/null
  fi
}

AUR_ARCH=() AUR_SOURCES="" AUR_SUMS=""
for A in $ARCHES; do
  case "$A" in
    x64) DEB=amd64 PAC=x86_64 ;;
    arm64) DEB=arm64 PAC=aarch64 ;;
    *) echo "arquitetura desconhecida: $A (use x64 ou arm64)" >&2; exit 2 ;;
  esac
  NAME="craftdriver-${VERSION}-linux-${A}"
  STAGING="$DIST/$NAME"
  mkdir -p "$STAGING"

  echo "→ publicando ($A, self-contained, trimming)"
  # obj/ limpo: o ILLink reaproveita a saída de um publish anterior com outras opções (medido: TrimMode ignorado)
  rm -rf "$ROOT"/src/*/obj/Release/net10.0/"linux-$A"
  "$DOTNET" publish "$CSPROJ" -c Release -r "linux-$A" --self-contained \
    -p:PublishTrimmed=true -p:DebugType=none -p:Version="$VERSION" -o "$STAGING/lib" >/dev/null
  mv "$STAGING/lib/CraftDriver.App" "$STAGING/lib/craftdriver" # o apphost acha CraftDriver.App.dll ao lado
  # modos uniformes (os .so do NuGet vêm 0744): 0644 em tudo, 0755 no executável, nas bibliotecas nativas e no createdump
  find "$STAGING/lib" -type f -exec chmod 644 {} +
  chmod 755 "$STAGING/lib/craftdriver" "$STAGING"/lib/*.so
  [ -f "$STAGING/lib/createdump" ] && chmod 755 "$STAGING/lib/createdump"
  ln -s lib/craftdriver "$STAGING/craftdriver"

  cp -r "$ROOT/packaging/share" "$STAGING/share"
  install -Dm644 "$ROOT/src/CraftDriver.App/Assets/app-256.png" "$STAGING/share/icons/hicolor/256x256/apps/craftdriver.png"
  chmod 755 "$STAGING/share/kio/servicemenus/craftdriver.desktop"
  # Extensões sem compilação (o plugin C++ do Dolphin é pacote separado, ver o cabeçalho)
  install -Dm644 "$ROOT/extensions/nautilus/craftdriver.py" "$STAGING/share/nautilus-python/extensions/craftdriver.py"
  install -Dm644 -t "$STAGING/share/file-manager/actions" "$ROOT"/extensions/pcmanfm/craftdriver-*.desktop
  install -Dm644 "$ROOT/extensions/thunar/uca.xml" "$STAGING/share/craftdriver/thunar/uca.xml"
  install -Dm755 "$ROOT/extensions/thunar/install-uca.py" "$STAGING/share/craftdriver/thunar/install-uca.py"
  cp "$ROOT/LICENSE" "$ROOT/README.md" "$ROOT/CHANGELOG.md" "$STAGING/"

  echo "→ tarball ($A)"
  tar -C "$DIST" -czf "$DIST/$NAME.tar.gz" "$NAME"
  (cd "$DIST" && sha256sum "$NAME.tar.gz" > "$NAME.tar.gz.sha256")
  SHA="$(cut -d' ' -f1 "$DIST/$NAME.tar.gz.sha256")"

  echo "→ .deb e .rpm ($A)"
  sed -e "s|@VERSION@|$VERSION|" -e "s|@STAGING@|$NAME|g" -e "s|@ARCH@|$DEB|" "$ROOT/packaging/nfpm.yaml" > "$DIST/nfpm-$A.yaml"
  run_nfpm deb "nfpm-$A.yaml"
  run_nfpm rpm "nfpm-$A.yaml"

  AUR_ARCH+=("'$PAC'")
  AUR_SOURCES+="source_${PAC}=(\"\${url}/releases/download/v\${pkgver}/craftdriver-\${pkgver}-linux-${A}.tar.gz\")"$'\n'
  AUR_SUMS+="sha256sums_${PAC}=('$SHA')"$'\n'
  rm -rf "$STAGING" "$DIST/nfpm-$A.yaml"
done

echo "→ PKGBUILD (AUR craftdriver-bin)"
ARCH_LIST="${AUR_ARCH[*]}" SOURCES="${AUR_SOURCES%$'\n'}" SUMS="${AUR_SUMS%$'\n'}" awk '
  { gsub(/@VERSION@/, ENVIRON["VERSION"]); gsub(/@ARCH@/, ENVIRON["ARCH_LIST"]) }
  /^@SOURCES@$/ { print ENVIRON["SOURCES"]; next }
  /^@SHA256SUMS@$/ { print ENVIRON["SUMS"]; next }
  { print }' "$ROOT/packaging/arch/PKGBUILD.in" > "$DIST/aur/PKGBUILD"
"$ROOT/packaging/arch/srcinfo.sh" "$DIST/aur"

echo "→ PKGBUILD (AUR craftdriver-dolphin, compilado da fonte pelo makepkg)"
mkdir -p "$DIST/aur/craftdriver-dolphin"
sed -e "s/@VERSION@/$VERSION/" "$ROOT/packaging/arch/craftdriver-dolphin/PKGBUILD.in" > "$DIST/aur/craftdriver-dolphin/PKGBUILD"
"$ROOT/packaging/arch/srcinfo.sh" "$DIST/aur/craftdriver-dolphin"

echo "→ pronto:"
ls -la "$DIST" "$DIST/aur" "$DIST/aur/craftdriver-dolphin"
