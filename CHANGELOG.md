# Changelog

Mudanças visíveis para quem usa o craftDriver. Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).
A seção de cada versão vira o texto da release no GitHub (`packaging/release-notes.sh`), com a tabela de instalação
acrescentada automaticamente.

## [Não lançado]

## [0.2.0] - 2026-11-05

### Novidades
- **Arquivos grandes abrem na hora**: só a parte lida é baixada (leitura por faixas); o resto vem em segundo plano
  e o arquivo só entra no cache depois de conferido.
- **Compartilhados comigo** (pasta ao lado de cada conta) e **atalhos** ("Adicionar a Meus arquivos") aparecem e
  podem ser editados quando há permissão.
- **Link de compartilhamento**, **Versões anteriores** e **Lixeira** pelo menu de contexto, pela bandeja e por
  `craftdriver share|versions|trash`.
- **Sincronização seletiva**: "Escolher pastas…" por conta e `craftdriver exclude|include`.
- Limites de **banda e downloads simultâneos** nas Configurações, aplicados sem reiniciar.
- Velocidade, tempo restante e **Cancelar** nas transferências; aba **Conflitos**.
- Notificações da área de trabalho (sessão expirada, conflitos, exclusão em massa) e lista do que foi apagado no
  aviso de exclusão em massa.
- Emblemas de status e menu no **Dolphin** (pacote `craftdriver-dolphin`), **Nautilus**, **Thunar** e **PCManFM**;
  fixar/liberar também pelo atributo `user.onedrive.pinned`.
- Interface: confirmação antes de desvincular conta, parar de sincronizar, liberar espaço e trocar a pasta;
  mensagens de erro destacadas; tema claro/escuro manual; atalhos de teclado (F5, Ctrl+, Ctrl+S, Ctrl+Q) e Esc nos
  diálogos; nomes para leitor de tela; a janela lembra tamanho e posição.
- Início: a janela aparece na hora, com "Abrindo o chaveiro do sistema…" e a opção de continuar sem ele.
- Erros de login em português, com "Tentar de novo".
- Pasta do cache editável e "Reiniciar agora" quando uma configuração só vale depois de reiniciar.

### Correções
- Cada envio de arquivo pequeno criava **duas versões** no OneDrive (conteúdo + data); agora cria uma.
- **ARM (arm64)**: dados de arquivo gravados no lugar errado para o kernel; e com libfuse 3.14 (Ubuntu 24.04,
  Debian 12) a abertura de arquivo lia além da estrutura da libfuse.
- Abrir ou truncar um arquivo enquanto o limite do cache o liberava podia dar erro de E/S.
- Uma instância de teste (`CRAFTDRIVER_HOME`) não usa mais os tokens do chaveiro da instalação normal.
- Perda de dados: escrita durante envio, renomear/apagar com envio pendente, truncar, hash divergente e outros casos
  (ver `PENDENCIAS.md`, seção 1).
- O sistema de arquivos não trava mais enquanto um arquivo baixa (FUSE de baixo nível, sem bloquear o laço);
  Ctrl+C interrompe um download em andamento.
- Números de inode estáveis entre montagens; cópia dentro da montagem por `copy_file_range`.
- Fechar a janela sem bandeja no painel não deixa o app invisível.
- `config.json` gravado com permissão 0600 e protegido contra versões mais novas do formato.

### Pacotes
- O app deixa de ser um executável único: vai como pasta em `/usr/lib/craftdriver`. Não extrai mais 14 MB em
  `~/.net/` a cada versão e funciona com `$HOME` só leitura.
- Publicado com trimming: instalado ocupa ~52 MB (antes 102 MB); download ~21 MB (antes ~43 MB).
- Dependências que faltavam: `xdg-utils` e `dbus-send` (obrigatórias); `notify-send` e `wl-copy`/`xclip`/`xsel`
  (recomendadas).
- `packaging/build.sh` gera também arm64 (`ARCHES="x64 arm64"`), testado no CI num executor ARM.

## [0.1.4] - 2026-11-04

### Correções
- Salvar planilhas e documentos deixava arquivos temporários na Lixeira do OneDrive.

  Editores como LibreOffice, OnlyOffice e WPS salvam em duas etapas: gravam num arquivo temporário e, logo depois,
  o renomeiam por cima do original. O craftDriver começava a enviar o temporário antes dessa renomeação, então ele
  aparecia na nuvem como arquivo novo e era apagado em seguida. O arquivo salvo sempre chegou correto, com o mesmo
  ID e o histórico de versões, mas cada salvamento gerava um envio a mais e deixava um item na Lixeira.

  Agora o envio só começa 3 s depois de o arquivo ser fechado. A exclusão de um item temporário que já tenha subido
  passa a mostrar o nome dele no registro, e não só o código.

## [0.1.3] - 2026-11-04

### Correções
- Logo após entrar na sessão, uma conta podia aparecer como "Entre novamente" por cerca de 30 s, enquanto o KWallet
  terminava de abrir. Agora o craftDriver tenta ler o chaveiro de novo por alguns segundos antes de pedir login.
- Abrir o craftDriver pelo menu enquanto uma instância `--headless` está rodando não fica mais sem resposta: aparece
  um aviso explicando como encerrá-la.
- `craftdriver --minimized` com o app já aberto não traz a janela para a frente (autostart duplicado).

## [0.1.2] - 2026-11-04

### Correções
- As contas pediam login de novo depois de reiniciar em sessões fora do Plasma/GNOME (Hyprland, Sway, i3…).
  - Na inicialização, o craftDriver aciona o serviço do chaveiro se ele não estiver rodando. No KWallet 6 o serviço
    `ksecretd` não sobe sozinho fora do Plasma.
  - Se você já entrou nas contas, o app espera o chaveiro, por até 60 s, em vez de passar a usar um arquivo de tokens
    vazio. Antes, isso fazia todas as contas pedirem login.
  - Se o chaveiro não responder, a mensagem diz isso, em vez de "Entre novamente".

### Hyprland / Sway com KWallet
Para a carteira abrir sem pedir senha, adicione ao autostart do compositor, antes do craftDriver:

```
exec-once = /usr/lib/pam_kwallet_init
```

É o que o Plasma faz ao iniciar a sessão. Requer o `kwallet-pam` e a senha da carteira igual à senha de login.

## [0.1.1] - 2026-11-04

### Novidades
- Pacotes **.deb**, **.rpm** e **Arch** (`craftdriver-bin`, PKGBUILD do AUR), testados em contêineres limpos de
  Ubuntu 24.04, Debian 12, Fedora e Arch. Todos trazem o .NET embutido; atalho no menu, ícone e menu de contexto do
  Dolphin vêm instalados.
- `craftdriver --version`.
- Instalado por pacote, **Instalar atalho…** não duplica atalho e menu do Dolphin que já vieram do sistema.
- O tarball passa a incluir atalho, ícone e menu do Dolphin em `share/`.
- CI: testes a cada push; pacotes gerados, testados e anexados à release a cada tag.

Vindo da instalação manual em `~/.local/opt/craftdriver`? Instale o pacote e remova a cópia antiga:
`rm -rf ~/.local/opt/craftdriver ~/.local/bin/craftdriver ~/.local/share/applications/craftdriver.desktop ~/.local/share/kio/servicemenus/craftdriver.desktop`.
Configuração e contas são mantidas.

## [0.1.0] - 2026-11-04

- Primeira versão: contas pessoais, corporativas e bibliotecas do SharePoint montadas em `~/OneDrive` via FUSE, com
  arquivos sob demanda, fixação ("Sempre manter neste dispositivo"), bandeja e menu do Dolphin.

[Não lançado]: https://github.com/Kaike-png/craftDriver/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/Kaike-png/craftDriver/compare/v0.1.4...v0.2.0
[0.1.4]: https://github.com/Kaike-png/craftDriver/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/Kaike-png/craftDriver/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/Kaike-png/craftDriver/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/Kaike-png/craftDriver/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/Kaike-png/craftDriver/releases/tag/v0.1.0
