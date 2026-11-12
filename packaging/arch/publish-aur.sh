#!/usr/bin/env bash
# Publicação opcional após a release: AUR_SSH_KEY deve ser de um mantenedor dos dois pacotes.
# Sem segredo, não acessa o AUR. Não altera o repositório do CraftDriver.
set -euo pipefail
if [[ -z "${AUR_SSH_KEY:-}" ]]; then echo "::warning::AUR_SSH_KEY ausente; publicação no AUR ignorada."; exit 0; fi
SOURCE=$(cd "${1:?pasta aur gerada por packaging/build.sh}" && pwd)
for dir in "$SOURCE" "$SOURCE/craftdriver-dolphin"; do
  [[ -s "$dir/PKGBUILD" && -s "$dir/.SRCINFO" ]] || { echo "PKGBUILD/.SRCINFO ausentes: $dir" >&2; exit 1; }
done
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
printf '%s\n' "$AUR_SSH_KEY" > "$TMP/key"; chmod 600 "$TMP/key"
unset AUR_SSH_KEY
# Usa apenas esta chave e este known_hosts; não toca ~/.ssh nem usa as chaves da sessão.
# Chave de host do AUR fixada (sem ssh-keyscan/TOFU): a impressão digital Ed25519 publicada em
# https://aur.archlinux.org (rodapé da página inicial) é SHA256:RFzBCUItH9LZS0cKB5UE6ceAYhBD5C8GeOBip8Z11+4.
# Se o AUR trocar a chave, a conexão falha (StrictHostKeyChecking=yes): confira a nova impressão no site e troque as
# duas linhas abaixo.
AUR_HOST_KEY='aur.archlinux.org ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIEuBKrPzbawxA/k2g6NcyV5jmqwJ2s+zpgZGZ7tpLIcN'
AUR_FINGERPRINT='SHA256:RFzBCUItH9LZS0cKB5UE6ceAYhBD5C8GeOBip8Z11+4'
printf '%s\n' "$AUR_HOST_KEY" > "$TMP/known_hosts"
ssh-keygen -lf "$TMP/known_hosts" | grep -qF " $AUR_FINGERPRINT " || { echo "chave fixada do AUR não confere com $AUR_FINGERPRINT" >&2; exit 1; }
export GIT_SSH_COMMAND="ssh -i $TMP/key -o IdentitiesOnly=yes -o UserKnownHostsFile=$TMP/known_hosts -o StrictHostKeyChecking=yes"
for pkg in craftdriver-bin craftdriver-dolphin; do
  dir=$SOURCE; [[ "$pkg" != craftdriver-dolphin ]] || dir="$SOURCE/craftdriver-dolphin"
  git clone "ssh://aur@aur.archlinux.org/$pkg.git" "$TMP/$pkg"
  cp "$dir/PKGBUILD" "$dir/.SRCINFO" "$TMP/$pkg/"
  (
    cd "$TMP/$pkg"
    git add PKGBUILD .SRCINFO
    if ! git diff --cached --quiet; then
      version=$(sed -n 's/^pkgver=//p' PKGBUILD)
      git -c user.name='CraftDriver CI' -c user.email='kaikedepaula8@gmail.com' commit -m "Atualiza para $version"
      git push origin HEAD
    else
      echo "$pkg já atualizado"
    fi
  )
done
