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
| Qualquer outra | `.tar.gz`: extraia e rode `./craftdriver` |

Depois abra **craftDriver** no menu de aplicativos e entre com a conta Microsoft. Para iniciar com a sessão,
marque **Configurações ▸ Iniciar ao entrar na sessão** (no Hyprland, adicione `exec-once = craftdriver --minimized`).
No Hyprland/Sway com KWallet, inicie também `/usr/lib/pam_kwallet_init` antes dele, para a carteira abrir com a senha do login
(o Plasma faz isso sozinho; sem isso o KWallet pede a senha ao iniciar).

Os pacotes são gerados e testados (Ubuntu 24.04, Debian 12, Fedora e Arch, em contêineres limpos) por
`packaging/build.sh` e `packaging/test/run.sh`; o GitHub Actions faz isso e anexa à release a cada tag `v*`.

## Requisitos

- Linux x86_64 (aarch64 compila, mas ainda não é testado), `fuse3` (`fusermount3`), `libsecret` (opcional, para o chaveiro)
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
    FuseHost.cs                binding P/Invoke da libfuse3 (fuse_new/fuse_mount/fuse_loop_mt), sem dependências
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

## Limitações conhecidas

- Sem ícones de sobreposição (nuvem/check) no Dolphin/Nautilus — use `getfattr` ou "Status do OneDrive" no menu de contexto
- Abrir um arquivo somente online baixa o arquivo inteiro antes de liberar a leitura (sem streaming por faixas)
- Mover entre drives diferentes vira copiar+apagar (`EXDEV`), como entre discos
- Atalhos "Adicionar a Meus arquivos" e pastas compartilhadas comigo ainda não aparecem
- Blocos de anotações do OneNote (pacotes) são ignorados
- Permissões/proprietário POSIX não são sincronizados (tudo `0644`/`0755` do usuário atual)
