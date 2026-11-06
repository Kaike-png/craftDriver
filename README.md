# craftDriver

Cliente para **OneDrive e SharePoint** no Linux, em **C# (.NET 10)** com interface gráfica **Avalonia**, no estilo do cliente do Windows:
arquivos **sob demanda** (aparecem na pasta sem ocupar espaço e são baixados ao abrir), "Sempre manter neste dispositivo",
"Liberar espaço", ícone na bandeja, contas pessoais e corporativas, e bibliotecas de documentos do SharePoint.

> craftDriver é um projeto independente, **não afiliado nem endossado pela Microsoft**.
> OneDrive, SharePoint e Microsoft 365 são marcas registradas da Microsoft Corporation.

## Funcionalidades

| Recurso | Como funciona |
|---|---|
| Login Microsoft | MSAL, fluxo de **código de dispositivo** (pessoal, trabalho/escola, MFA). Tokens no **chaveiro do sistema** (Secret Service/KWallet), fallback para arquivo `chmod 600` |
| Arquivos sob demanda | Sistema de arquivos **FUSE3** montado em `~/OneDrive`. Listagem vem do banco local; conteúdo é baixado no primeiro `open()` |
| Várias contas / drives | Cada drive vira uma pasta: `~/OneDrive/OneDrive - Pessoal`, `~/OneDrive/OneDrive - empresa`, `~/OneDrive/Financeiro - Documentos` |
| SharePoint | Sites seguidos, busca por nome, ou URL do site → escolhe a biblioteca |
| Sincronização bidirecional | Nuvem→local via **delta** (polling configurável). Local→nuvem via **fila persistente** (SQLite), ordem garantida, funciona offline e reenvia ao reconectar |
| Uploads | Até 4 MB em PUT simples; maiores em **sessão de upload** (blocos de 10 MiB). Preserva data de modificação |
| Exclusão em massa | Apagar mais de N itens de uma drive de uma vez (padrão 50) **retém** as exclusões na nuvem e pede confirmação (janela, notificação, `craftdriver deletes confirm\|restore`). "Restaurar" traz os itens de volta da nuvem. A retenção sobrevive a reinícios |
| Exclusões recursivas | `rm -r`/Dolphin apagam arquivo por arquivo; o app espera 3 s sem novas exclusões e envia só a da pasta (o conteúdo vai junto para a Lixeira do OneDrive) |
| Conflitos | Edição dos dois lados → a sua versão vira `nome (conflito - host data).ext` (If-Match/eTag). Criação com mesmo nome idem |
| Salvamento atômico | `sed -i`, gedit, kate, LibreOffice (temp + rename) viram **nova versão do mesmo item** (preserva histórico de versões) |
| Sempre disponível / Liberar espaço | Pela interface, pela CLI ou pelo menu de contexto do Dolphin/Nautilus/Nemo |
| Link de compartilhamento | Menu de contexto "Copiar link de compartilhamento" ou `craftdriver share [--edit] arquivo`: cria o link (`createLink`), copia (wl-copy no Wayland; xclip/xsel no X11) e notifica com "Abrir no navegador". Tenta "qualquer pessoa com o link"; se a organização proíbe, cai para "pessoas da organização" e avisa |
| Versões anteriores | Menu "Versões anteriores…" ou `craftdriver versions arquivo`: janela com as versões da nuvem (data, tamanho, autor), **Restaurar** (vira a versão atual; nenhuma é apagada) e **Baixar cópia** (`nome (versão AAAA-MM-DD HHMM).ext` ao lado). Recusa restaurar se o arquivo tem alterações locais ainda não enviadas |
| Lixeira | `craftdriver trash` / bandeja "Lixeira…": lista o que o craftDriver viu ser excluído; no OneDrive pessoal restaura pela API, nas contas corporativas/SharePoint abre a Lixeira no navegador (a API de lá ainda é beta) |
| Limite de cache | LRU para arquivos não fixados (padrão 10 GB) |
| Indexadores bloqueados | Baloo, Tracker, thumbnailers etc. **não** disparam downloads de itens somente online |
| Arquivos só locais | `.~lock.*#`, `~$*`, `*.swp`, `.goutputstream-*`… e nomes inválidos no OneDrive nunca sobem |
| Status por arquivo | `getfattr -n user.onedrive.status arquivo` (OnlineOnly, Available, AlwaysAvailable, Syncing, LocalOnly). `du` mostra 0 para itens só na nuvem |
| Bandeja | Ícone por estado (atualizado, sincronizando, pausado, offline, erro); fechar a janela mantém o app rodando |

## Instalação

Baixe o pacote da sua distro na [última release](https://github.com/Kaike-png/craftDriver/releases/latest).
Todos trazem o .NET embutido; atalho no menu, ícone e menu de contexto do Dolphin já vêm instalados.

| Distro | Como instalar |
|---|---|
| Arch, Garuda, Manjaro, EndeavourOS | `yay -S craftdriver-bin` (AUR) ou `sudo pacman -U craftdriver-bin-*.pkg.tar.zst` |
| Debian, Ubuntu, Mint, Pop!_OS | `sudo apt install ./craftdriver_*_amd64.deb` |
| Fedora, openSUSE | `sudo dnf install ./craftdriver-*.x86_64.rpm` |
| Qualquer outra | `.tar.gz`: extraia e rode `./craftdriver` (o app fica em `lib/`) |

Instalado, ocupa ~52 MB em `/usr/lib/craftdriver` (o .NET vai junto, já reduzido). O que mudou em cada versão
está no [CHANGELOG](CHANGELOG.md).

Depois abra **craftDriver** no menu de aplicativos e entre com a conta Microsoft. Para iniciar com a sessão,
marque **Configurações ▸ Iniciar ao entrar na sessão** (no Hyprland, adicione `exec-once = craftdriver --minimized`).
No Hyprland/Sway com KWallet, inicie também `/usr/lib/pam_kwallet_init` antes dele, para a carteira abrir com a senha do login
(o Plasma faz isso sozinho; sem isso o KWallet pede a senha ao iniciar).

Os pacotes são gerados e testados (Ubuntu 24.04, Debian 12, Fedora e Arch, em contêineres limpos) por
`packaging/build.sh` e `packaging/test/run.sh`; o GitHub Actions faz isso e anexa à release a cada tag `v*`.
`ARCHES="x64 arm64" packaging/build.sh` gera também os pacotes ARM; detalhes e medições em
[`docs/empacotamento.md`](docs/empacotamento.md).

## Extensões de gerenciador de arquivos

Ficam em [`extensions/`](extensions). Todas leem o atributo `user.onedrive.status` que o FUSE expõe, então
funcionam com qualquer ponto de montagem, e chamam `craftdriver pin|unpin|status|share --notify` e `craftdriver versions`.

| Gerenciador | Emblemas | Menu só dentro do craftDriver | Como instalar |
|---|---|---|---|
| Dolphin (plugin KF6) | sim | sim | AUR `craftdriver-dolphin`, ou pacote separado `.deb` (Debian 13 / Ubuntu 25.04) e `.rpm` (Fedora 41) da release, específico da distro; para compilar: `cmake -S extensions/dolphin -B build -DCMAKE_INSTALL_PREFIX=/usr && cmake --build build && sudo cmake --install build` (precisa de `extra-cmake-modules` e dos headers do KIO 6) |
| Dolphin (servicemenu, já vem nos pacotes) | não | não: aparece em qualquer arquivo | instalado pelo pacote/app; o plugin acima o esconde (`/etc/xdg/kservicemenurc`) |
| Nautilus 42+ (API 4.0; fallback 3.0) | sim | sim | pacotes instalam em `/usr/share/nautilus-python/extensions/`; manual: copie `extensions/nautilus/craftdriver.py` para `~/.local/share/nautilus-python/extensions/`. Precisa de `python-nautilus` (Debian/Ubuntu: `python3-nautilus`; Fedora: `nautilus-python`). Depois `nautilus -q` |
| PCManFM / PCManFM-Qt | não | sim (`ShowIfTrue` + `getfattr`, pacote `attr`) | pacotes instalam em `/usr/share/file-manager/actions/`; manual: copie `extensions/pcmanfm/*.desktop` para `~/.local/share/file-manager/actions/` |
| Thunar | não | não: o Thunar só filtra por nome/tipo | `python3 /usr/share/craftdriver/thunar/install-uca.py` (ou `extensions/thunar/install-uca.py`); `--remove` desfaz; `--command CAMINHO` troca o `craftdriver` do início dos comandos (ex.: `--command ~/.local/bin/craftdriver` quando `~/.local/bin` não está no PATH da sessão). O `install-user.sh` já roda com o caminho absoluto se o Thunar estiver instalado ou as ações já existirem |

Emblemas no Dolphin (tema Breeze): nuvem = somente online, ✓ = disponível, ✓ verde = sempre disponível,
seta laranja = sincronizando, ✗ cinza = somente local. O Dolphin precisa ser reaberto depois de instalar o plugin.
Só o plugin do Dolphin foi testado num Dolphin real; Nautilus, Thunar e PCManFM têm só testes sem o
gerenciador (`python3 -m unittest discover -s extensions/<nome>/tests`).

## Requisitos

- Linux x86_64. aarch64: os pacotes são gerados por compilação cruzada e os testes rodam no CI num executor ARM,
  mas o app nunca foi usado num ARM de verdade
- `fuse3` (`fusermount3`), `xdg-utils`, `dbus-send`; opcionais: `libsecret` (chaveiro), `notify-send`,
  `wl-copy`/`xclip`/`xsel` (copiar link)
- .NET SDK 10 para compilar (`~/.dotnet/dotnet` serve)
- Para o ícone na bandeja: um host StatusNotifierItem (KDE, GNOME com extensão AppIndicator, waybar/quickshell no Hyprland)

## Compilar, testar, executar

```bash
dotnet build
dotnet test                       # motor + FUSE real + telas (Avalonia headless)
dotnet run --project src/CraftDriver.App
./install.sh                      # publica em ~/.local/opt/craftdriver e cria ~/.local/bin/craftdriver
```

## Linha de comando

```bash
craftdriver                    # interface (instância única; abre a janela existente)
craftdriver --minimized        # direto na bandeja
craftdriver --headless         # sem interface (servidor)
craftdriver login              # adiciona conta pelo terminal
craftdriver status [arquivos]  # status geral ou de itens
craftdriver pin   <arquivos>   # sempre manter neste dispositivo
craftdriver unpin <arquivos>   # liberar espaço (somente online)
craftdriver deletes [confirm|restore]   # exclusão em massa aguardando confirmação
craftdriver share [--edit] <arquivos>   # link de compartilhamento (copiado para a área de transferência)
craftdriver versions <arquivo> [--list | --restore=ID | --copy=ID]   # versões anteriores
craftdriver trash [restore N | open]    # excluídos recentes / restaurar da Lixeira
craftdriver sync | pause | resume | quit
```

## Arquitetura

```
src/
  CraftDriver.Core/
    Auth/AuthService.cs        MSAL + cache de tokens (chaveiro)
    Graph/GraphClient.cs       REST da Graph: delta, download, upload (simples/sessão), pastas, mover, SharePoint
    Graph/QuickXorHash.cs      hash do OneDrive (validado com vetores do rclone)
    State/StateDb.cs           SQLite: itens (lid local estável ↔ id remoto), drives, fila de operações
    Sync/SyncEngine.cs         por drive: loop de delta, fila (upload/mkdir/move/delete/settime), conflitos, fixados, LRU
    Vfs/VirtualFs.cs           semântica POSIX → banco + fila (independente do FUSE; testável)
    Ipc/Ipc.cs                 socket Unix para CLI e menus do gerenciador de arquivos
  CraftDriver.Fuse/
    FuseHost.cs                binding P/Invoke low-level da libfuse3 (fuse_session_new/fuse_session_mount/fuse_session_loop_mt), sem dependências
  CraftDriver.App/           Avalonia 12: janela, bandeja, diálogos de login e SharePoint, integração com o desktop
tests/
  CraftDriver.Tests/         Graph falso em memória + testes do motor + montagem FUSE real com bash/sed/cp/mv/rm
  CraftDriver.UiTests/       renderização headless de todas as telas
```

Arquivos de runtime: `~/.config/craftdriver/config.json`, `~/.local/state/craftdriver/{state.db,craftdriver.log}`,
`~/.cache/craftdriver/content/` (conteúdo baixado/editado). `CRAFTDRIVER_HOME=/dir` isola tudo num diretório (testes/perfis).

## Client ID

O app usa por padrão o Client ID do registro **craftDriver** deste projeto no Microsoft Entra ID:
`979715f8-d815-4f25-ac64-05cbccc74cc2`. Alguns tenants corporativos exigem consentimento do administrador
para apps de terceiros. Para usar outro registro, crie um em [Entra ID](https://entra.microsoft.com) →
*App registrations* → *New registration*:

1. *Supported account types*: contas em qualquer diretório organizacional **e** contas Microsoft pessoais
2. *Authentication* → *Add a platform* → *Mobile and desktop* → marque `https://login.microsoftonline.com/common/oauth2/nativeclient`
   e adicione o URI personalizado `http://localhost` (login automático das contas pessoais)
3. *Allow public client flows* = **Yes** (necessário para o código de dispositivo das contas de trabalho)
4. *API permissions* (delegadas, Microsoft Graph): `Files.ReadWrite.All`, `Sites.ReadWrite.All`, `offline_access`
   (contas pessoais pedem só `Files.ReadWrite.All`: pedir `Sites.*` a uma conta pessoal faz o microsoft.com/link
   responder "O código inserido expirou")
5. Cole o *Application (client) ID* em Configurações ▸ Avançado e entre novamente nas contas

## Migração do nome antigo

Versões anteriores se chamavam `onedrive-linux`. Na primeira execução, o craftDriver move
`~/.config/onedrive-linux`, `~/.local/state/onedrive-linux` e `~/.cache/onedrive-linux` para `craftdriver`
e mantém os tokens salvos no chaveiro (não é preciso entrar de novo).

## Créditos

- [jstaf/onedriver](https://github.com/jstaf/onedriver) (GPL-3.0) e [abraunegg/onedrive](https://github.com/abraunegg/onedrive)
  (GPL-3.0): referência de arquitetura e de regras da API (modelo FUSE sob demanda, delta, nomes, SharePoint).
  Nenhum código foi copiado desses projetos.
- QuickXorHash: porta do exemplo de referência publicado pela Microsoft na documentação da API do OneDrive.
- Vetores de teste do QuickXorHash: [rclone](https://github.com/rclone/rclone) (MIT).
- Bibliotecas: MSAL.NET, Avalonia, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite (MIT) e libfuse3 (LGPL-2.1, carregada dinamicamente).

## Licença

[MIT](LICENSE)

Tarball: extraia **a pasta inteira** e rode `./install-user.sh` para instalar em `~/.local` (binário, atalho,
ícones e menus; sem SDK e sem sudo). `./install-user.sh --uninstall` remove a instalação (inclusive as ações do Thunar) e mantém os seus dados. Com o
app aberto, os dois encerram a instância e esperam ela desmontar e sair (até 30 s, `CRAFTDRIVER_QUIT_TIMEOUT`); se
ela não sair, a instalação é abortada sem mexer na versão atual. Menus do Dolphin, PCManFM e Thunar apontam para o
caminho absoluto `~/.local/bin/craftdriver` (não exigem `~/.local/bin` no PATH da sessão).
Não remova `lib/`: o executável não é single-file. Para compilar a fonte, `./install.sh` exige SDK .NET 10.

## Limitações conhecidas

- Emblemas de status só no Dolphin (com o plugin `craftdriver-dolphin`) e no Nautilus (com `python-nautilus`); nos outros, use `getfattr` ou "Status no craftDriver" no menu de contexto
- Streaming por faixas existe, mas libfuse < 3.17.3 (ou kernel sem `FUSE_CAP_DIRECT_IO_ALLOW_MMAP`) usa o cache de páginas em vez de `direct_io`: erros de leitura podem chegar como `EIO`; veja [streaming](docs/streaming.md)
- Mover entre drives diferentes vira copiar+apagar (`EXDEV`), como entre discos
- Atalhos e “Compartilhados comigo” existem; a integração Graph com SharePoint/conta de trabalho e as permissões reais desses itens ainda não foram verificadas ponta a ponta com uma conta real
- Blocos de anotações do OneNote (pacotes) são ignorados
- Permissões/proprietário POSIX não são sincronizados (tudo `0644`/`0755` do usuário atual)
