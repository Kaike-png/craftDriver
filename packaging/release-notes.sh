#!/usr/bin/env bash
# Imprime o texto da release de uma tag: a seção da versão no CHANGELOG.md + tabela de instalação.
# Falha (rc 1, mensagem no stderr) se a versão não tiver seção — o CI não publica release sem notas.
# Uso: packaging/release-notes.sh v0.1.5 [CHANGELOG.md]
set -euo pipefail
TAG="${1:?uso: release-notes.sh vX.Y.Z [CHANGELOG.md]}"
VER="${TAG#v}"
CHANGELOG="${2:-$(cd "$(dirname "$0")/.." && pwd)/CHANGELOG.md}"

body="$(awk -v ver="$VER" '
  /^## \[/ { if (inside) exit; if (index($0, "## [" ver "]") == 1) { inside = 1; next } }
  /^\[[^]]+\]: / { if (inside) exit }
  inside { print }' "$CHANGELOG")"
# tira linhas em branco do começo e do fim
body="$(printf '%s\n' "$body" | sed -e '/./,$!d' | tac | sed -e '/./,$!d' | tac)"
if [ -z "$body" ]; then
  echo "release-notes: $CHANGELOG não tem a seção \"## [$VER]\" (ou ela está vazia)" >&2
  exit 1
fi

printf '%s\n\n' "$body"
cat <<EOF
## Instalar
| Distro | Comando |
|---|---|
| Arch, Garuda, Manjaro, EndeavourOS | \`sudo pacman -U craftdriver-bin-${VER}-1-x86_64.pkg.tar.zst\` |
| Debian, Ubuntu, Mint, Pop!_OS | \`sudo apt install ./craftdriver_${VER}-1_amd64.deb\` |
| Fedora, openSUSE | \`sudo dnf install ./craftdriver-${VER}-1.x86_64.rpm\` |
| Qualquer outra | \`craftdriver-${VER}-linux-x64.tar.gz\`: extraia e rode \`./craftdriver\` |

Para ARM (aarch64), use os arquivos com \`arm64\`/\`aarch64\` no nome, quando presentes. Checksums nos arquivos \`.sha256\`.
EOF
