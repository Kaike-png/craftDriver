#!/usr/bin/env bash
# Gera os artefatos de distribuição do craftDriver em dist/:
#   craftdriver-<ver>-linux-x64.tar.gz (+ .sha256)   binário + arquivos de desktop
#   craftdriver_<ver>-1_amd64.deb                     Debian/Ubuntu
#   craftdriver-<ver>-1.x86_64.rpm                    Fedora/openSUSE
#   PKGBUILD + .SRCINFO                               AUR (craftdriver-bin)
#
# Uso: packaging/build.sh [versão]   (padrão: <Version> do CraftDriver.App.csproj)
# Requer: .NET SDK 10 e Docker (para o nfpm; ou nfpm instalado no PATH).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CSPROJ="$ROOT/src/CraftDriver.App/CraftDriver.App.csproj"
VERSION="${1:-$(grep -oP '(?<=<Version>)[^<]+' "$CSPROJ")}"
DIST="$ROOT/dist"
NAME="craftdriver-${VERSION}-linux-x64"
STAGING="$DIST/$NAME"
DOTNET="$(command -v dotnet || echo "$HOME/.dotnet/dotnet")"

echo "→ craftDriver $VERSION"
rm -rf "$DIST"
mkdir -p "$STAGING"

echo "→ publicando binário (linux-x64, self-contained, single-file)"
"$DOTNET" publish "$CSPROJ" -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -p:Version="$VERSION" -o "$DIST/publish" >/dev/null
install -m755 "$DIST/publish/CraftDriver.App" "$STAGING/craftdriver"
rm -rf "$DIST/publish"

cp -r "$ROOT/packaging/share" "$STAGING/share"
install -Dm644 "$ROOT/src/CraftDriver.App/Assets/app-256.png" "$STAGING/share/icons/hicolor/256x256/apps/craftdriver.png"
chmod 755 "$STAGING/share/kio/servicemenus/craftdriver.desktop"
cp "$ROOT/LICENSE" "$ROOT/README.md" "$STAGING/"

echo "→ tarball"
tar -C "$DIST" -czf "$DIST/$NAME.tar.gz" "$NAME"
(cd "$DIST" && sha256sum "$NAME.tar.gz" > "$NAME.tar.gz.sha256")
SHA="$(cut -d' ' -f1 "$DIST/$NAME.tar.gz.sha256")"

echo "→ .deb e .rpm (nfpm)"
sed -e "s|@VERSION@|$VERSION|" -e "s|@STAGING@|$NAME|g" "$ROOT/packaging/nfpm.yaml" > "$DIST/nfpm.yaml"
run_nfpm() {
  if command -v nfpm >/dev/null; then
    (cd "$DIST" && nfpm package -f nfpm.yaml -p "$1" -t .)
  else
    docker run --rm -u "$(id -u):$(id -g)" -v "$DIST:/work" -w /work \
      goreleaser/nfpm package -f nfpm.yaml -p "$1" -t . >/dev/null
  fi
}
run_nfpm deb
run_nfpm rpm

echo "→ PKGBUILD (AUR craftdriver-bin)"
mkdir -p "$DIST/aur"
sed -e "s/@VERSION@/$VERSION/" -e "s/@SHA256@/$SHA/" "$ROOT/packaging/arch/PKGBUILD.in" > "$DIST/aur/PKGBUILD"
cat > "$DIST/aur/.SRCINFO" <<EOF
pkgbase = craftdriver-bin
	pkgdesc = Cliente para OneDrive e SharePoint com arquivos sob demanda (FUSE). Não afiliado à Microsoft.
	pkgver = $VERSION
	pkgrel = 1
	url = https://github.com/Kaike-png/craftDriver
	arch = x86_64
	license = MIT
	depends = glibc
	depends = libgcc
	depends = libstdc++
	depends = fuse3
	depends = icu
	depends = fontconfig
	depends = libx11
	depends = libice
	depends = libsm
	depends = hicolor-icon-theme
	optdepends = libsecret: guardar os tokens no chaveiro do sistema (KWallet/GNOME Keyring)
	optdepends = dolphin: menu de contexto craftDriver no gerenciador de arquivos
	optdepends = noto-fonts-emoji: ícones de status na janela
	provides = craftdriver
	conflicts = craftdriver
	options = !strip
	options = !debug
	source = https://github.com/Kaike-png/craftDriver/releases/download/v$VERSION/$NAME.tar.gz
	sha256sums = $SHA

pkgname = craftdriver-bin
EOF

rm -rf "$STAGING" "$DIST/nfpm.yaml"
echo "→ pronto:"
ls -la "$DIST" "$DIST/aur"
