# Empacotamento: formato do app, tamanho e dependências

Medições de 2026-11-05 (i5-1235U, .NET SDK 10, Avalonia 12.1.5, MSAL 4.92). `--help` cronometrado 5×, com `HOME` vazio.

## Formato

| Publicação | Instalado | tar.gz | 1ª abertura | depois | `~/.net` | `$HOME` só leitura |
|---|---|---|---|---|---|---|
| single-file + `IncludeNativeLibrariesForSelfExtract` (até 0.1.4) | 102,8 MB | 43,6 MB | 0,09 s | 0,09 s | 14 MB por versão, nunca apagados | **não abre** (rc 159: *Default extraction directory … not accessible*) |
| single-file + `EnableCompressionInSingleFile` | 50,9 MB | 43,6 MB | 0,48 s | 0,24 s | 14 MB | não abre |
| pasta comum | 111,8 MB | 47,1 MB | 0,05 s | 0,06 s | nada | abre |
| pasta comum + ReadyToRun | 129,3 MB | — | — | — | nada | abre |
| **pasta + `PublishTrimmed` (escolhido)** | **52,5 MB** | **21,1 MB** | — | — | nada | abre |

A compressão do single-file só reduz o espaço instalado (o download já é comprimido) e custa 0,15–0,4 s em toda
abertura, sem resolver a extração. A pasta comum resolve a extração e o `$HOME` só leitura; o trimming devolve o
tamanho. Os pacotes já tinham `/usr/lib/craftdriver`, então só o conteúdo mudou (`/usr/bin/craftdriver` continua
sendo um link para `/usr/lib/craftdriver/craftdriver`).

`TrimMode=partial` não muda nada aqui: o MSAL, o Avalonia, o SQLite e o Tmds.DBus se declaram compatíveis com
trimming e são cortados nos dois modos (MSAL 1,28 → 0,70 MB).

## Segurança do trimming

- **Análise estática**: `CraftDriver.Core`, `CraftDriver.Fuse` e `CraftDriver.App` têm `EnableTrimAnalyzer` e tratam os avisos de
  trimming (IL2026, IL2104, IL3050…) como erro de build. Os únicos usos encontrados eram JSON por reflexão
  (`config.json`, IPC, `add-account`), trocados por `JsonSerializerContext` (`Config/JsonContexts.cs`). O formato
  gravado é idêntico ao anterior (`JsonFormatTests` compara com a serialização por reflexão). O `GraphClient` lê
  com `JsonDocument`, que não depende de reflexão.
- **Execução** (binário com trimming × sem trimming, mesma saída):
  - `login --work`: o MSAL busca o código de dispositivo na Microsoft e mostra "Abra … e digite o código".
  - `login --personal`: o MSAL gera a URL de autorização, abre o listener local, recebe o retorno (GET e POST
    `form_post`), mostra "Pronto! Login recebido", troca o código falso no endpoint de token e lê o erro real
    (`invalid_grant`, AADSTS70000), traduzido pelo `LoginErrors`.
  - `packaging/test/run.sh` (Ubuntu 24.04, Debian 12, Fedora, Arch): instala o pacote, `--version`, `--headless`
    com montagem FUSE, `status` pelo IPC, desmontagem e a janela no Xvfb.
- **Não verificado**: login completo com uma conta real, chamadas à Graph com token válido e o chaveiro de verdade
  (libsecret/KWallet) com o binário com trimming. O caminho de rede do MSAL e da Graph é o mesmo testado acima,
  mas uma resposta com campos que a análise não viu só apareceria com uma conta.
- Diagnóstico: `CRAFTDRIVER_MSAL_LOG=1` manda o log do MSAL (sem dados pessoais) para o stderr.

## Globalização

`InvariantGlobalization` dispensaria a `libicu`, mas a interface em pt-BR mudaria:

| | com ICU | invariante |
|---|---|---|
| `1234567.ToString("N0")` | 1.234.567 | 1,234,567 |
| `1.5.ToString("0.#")` | 1,5 | 1.5 |
| data `g` | 05/11/2026 14:03 | 11/05/2026 14:03 |
| ordenação | abacate, árvore, Ônibus, Zebra | Zebra, abacate, Ônibus, árvore |

Por isso a `libicu` continua como dependência.

## Dependências externas

Programas que o app chama (procurados no código, não supostos):

| Programa | Para quê | Debian/Ubuntu | Fedora | Arch | Tipo |
|---|---|---|---|---|---|
| `fusermount3` | montar | `fuse3` | `fuse3` | `fuse3` | obrigatório |
| `xdg-open` | abrir pasta, navegador do login, links | `xdg-utils` | `xdg-utils` | `xdg-utils` | obrigatório |
| `dbus-send` | iniciar o chaveiro, mostrar arquivo no gerenciador | `dbus-bin` | `dbus-tools` | `dbus` | obrigatório |
| `notify-send` | notificação, se o serviço não responder pelo D-Bus | `libnotify-bin` | `libnotify` | `libnotify` | recomendado |
| `wl-copy` / `xclip` / `xsel` | copiar link de compartilhamento | `wl-clipboard` … | idem | idem | recomendado |
| `getfattr` | ações do PCManFM | `attr` | `attr` | `attr` | sugerido |

Nomes conferidos em `ubuntu:24.04`, `debian:12`, `fedora:latest` (`dnf provides`) e no Arch (`pacman -Qo`).

## Arquiteturas

`ARCHES="x64 arm64" packaging/build.sh` publica as duas por compilação cruzada (o binário arm64 é ELF aarch64,
conferido com `file`) e gera um PKGBUILD com `source_x86_64`/`source_aarch64`. O layout de `struct stat` e de
`fuse_config` para aarch64 está no `FuseHost` mas só é exercitado pelos testes no executor ARM do CI; esta máquina
não tem emulação arm64 no Docker. AppImage e Flatpak não são gerados (o Flatpak não tem acesso ao FUSE do sistema).

`WM_CLASS` real da janela (Xvfb, pacote instalado): `("craftdriver", "CraftDriver.App")`, então
`StartupWMClass=CraftDriver.App` do `.desktop` bate com a classe.


## Runtime, TLS e modo usuário

Os pacotes declaram OpenSSL e `ca-certificates`, além de ICU, zlib e libstdc++. Debian 12 usa `libssl3`;
Debian 13 e Ubuntu 24.04 usam `libssl3t64` (`libssl3t64 | libssl3` no .deb); Fedora usa `openssl-libs`;
Arch usa `openssl`. Kerberos (`libgssapi-krb5-2`/`krb5-libs`/`krb5`) é recomendado/opcional: o OAuth do
OneDrive não usa Negotiate. `gcc-libs` no Arch também atende os derivados que ainda não dividiram o GCC.
OpenSSL é carregado por `dlopen`, portanto `ldd` sem “not found” não prova que o login funciona.

`craftdriver diagnose --tls [URL]` confere OpenSSL, o repositório de certificados raiz e faz um HEAD HTTPS
(padrão GitHub, sem conta/Microsoft; `--offline` pula só a rede). O smoke roda esse comando antes de montar.
Não desabilita validação de certificado. Uma resposta HTTP 403/404 ainda prova o handshake TLS; erro TLS/DNS
ou timeout faz o comando falhar. Não é um teste de autenticação nem da Graph.

`install.sh` publica uma pasta com trimming, igual ao `build.sh`, e delega ao `packaging/install-user.sh`.
Esse segundo script também vai na raiz do tarball como `install-user.sh` e não exige SDK. Instala o executável
em `~/.local/opt/craftdriver/lib`, comando em `~/.local/bin`, atalho, SVGs/PNG, Nautilus, Thunar e servicemenus.
A desinstalação remove esses arquivos, autostart, unit e a extração single-file legada `~/.net/CraftDriver.App`;
configuração, estado e cache ficam. Respeita `XDG_DATA_HOME`/`XDG_CONFIG_HOME`.

## AppStream, releases e Dolphin KF6

`python3 packaging/metainfo.py` gera `<releases>` (todas as versões, datas e notas) do `CHANGELOG.md`.
`--check [--tag vX.Y.Z]` falha se o XML versionado estiver velho ou se tag, `<Version>` do csproj, última release
AppStream e seção/data do CHANGELOG divergirem. O build verifica e gera o XML de staging; o CI valida com
`appstreamcli`, `desktop-file-validate` e `actionlint`, exige build sem avisos e salva TRX/Sequence/dump mini
se um teste ficar 5 minutos sem responder. Cobertura continua informativa: não há limiar arbitrário sem
estabelecer uma linha de base estável por arquitetura/FUSE (os testes podem ser ignorados conforme o kernel).

O plugin é um pacote **separado**: `packaging/dolphin/build-native.sh <distro>` compila e testa dentro de
Debian 13, Ubuntu 25.04 e Fedora 41, instala em DESTDIR e gera .deb/.rpm com dependências da própria distro
(`dpkg-shlibdeps` ou geração automática do RPM). O CI anexa esses artefatos à release; não use o pacote de
uma distro em outra (ABI KF6/Qt). Ubuntu 24.04/22.04 e Debian 12 não têm KF6 e continuam com servicemenu.
O PKGBUILD fonte declara x86_64 e aarch64; o build ARM do plugin depende de execução em hardware ARM.

A release só baixa artefatos de pacotes (não cobertura, screenshots ou relatórios) e seleciona os formatos
explicitamente. O job AUR roda depois dela: sem `AUR_SSH_KEY` avisa e pula; com a chave de mantenedor dos
pacotes `craftdriver-bin` e `craftdriver-dolphin`, atualiza PKGBUILD/.SRCINFO. Nenhuma publicação AUR é feita
por testes locais. O primeiro acesso SSH confia na chave coletada pelo `ssh-keyscan` do AUR no runner.

## Serviço de usuário

A unit inicia depois de `graphical-session.target` e para junto com ele (`PartOf`), sem deixar de servir
sessões tiling que só usam `default.target`. Herda WAYLAND_DISPLAY, DISPLAY e DBUS_SESSION_BUS_ADDRESS do
ambiente do gerenciador `systemd --user`; em sessões manuais, importe após iniciar o compositor:

```sh
systemctl --user import-environment WAYLAND_DISPLAY DISPLAY DBUS_SESSION_BUS_ADDRESS XDG_CURRENT_DESKTOP
```

`ExecStop` pede `craftdriver quit` e aguarda o PID sair antes de o systemd mandar SIGTERM (limite de 60 s).
Isso evita matar o motor logo após o ACK do IPC, antes de desmontar e concluir os envios. Código 143 não
provoca reinício por falha. Isso não autentica nenhuma conta nem inicia uma sessão gráfica.
