#!/usr/bin/env bash
# Publicação opcional após a release: AUR_SSH_KEY deve ser de um mantenedor dos dois pacotes.
# Sem segredo, não acessa o AUR. Não altera o repositório do craftDriver.
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
ssh-keyscan -t ed25519 aur.archlinux.org > "$TMP/known_hosts"
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
      git -c user.name='craftDriver CI' -c user.email='kaikedepaula8@gmail.com' commit -m "Atualiza para $version"
      git push origin HEAD
    else
      echo "$pkg já atualizado"
    fi
  )
done
