# Changelog

Mudanças visíveis para quem usa o CraftDriver. Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).
A seção de cada versão vira o texto da release no GitHub (`packaging/release-notes.sh`), com a tabela de instalação
acrescentada automaticamente.

## [Não lançado]

## [0.4.1] - 2026-11-12

### Correções
- **Planilhas e documentos do Office no SharePoint viravam "conflito de edição" a cada salvamento**, sem ninguém mais
  editar. Ao substituir um .xlsx/.docx/.pptx o CraftDriver mandava a data de modificação junto, e a biblioteca do
  SharePoint recusa isso: o envio falhava e a versão do arquivo na nuvem avançava, então a tentativa seguinte era
  tratada como edição dos dois lados. Agora a data fica a cargo do SharePoint (a que está dentro do documento).
- Dois salvamentos seguidos (ex.: salvamento automático) também podiam gerar cópia de conflito: uma leitura da nuvem
  feita durante o envio era aplicada depois dele e voltava o arquivo para a versão anterior.

### Mudanças de comportamento
- O nome passa a ser escrito **CraftDriver** na interface, nos menus dos gerenciadores de arquivos, nas notificações e
  na documentação. O comando `craftdriver`, os pacotes, as pastas de configuração e as contas já conectadas continuam
  iguais: não é preciso entrar de novo. Scripts antigos do Nautilus/Nemo com o nome anterior são substituídos.

## [0.4.0] - 2026-11-10

### Novidades
- **Interface redesenhada**: navegação numa barra lateral com a marca e o estado da sincronização sempre à vista
  (a cor do quadro é a mesma do ícone da bandeja), Sincronizar e Pausar à mão em qualquer aba, e Problemas/Conflitos
  com o ícone destacado quando há itens. Cada página abre com título, explicação e a ação principal; contas, pastas
  e listas viram linhas num painel, com as ações embaixo de cada item. Configurações em seções, com Salvar fixo no
  rodapé. Fonte IBM Plex Sans embutida, cores do ícone do craftDriver nos temas claro e escuro.
- **Itens fixados continuam na pasta com o craftDriver encerrado**, como no OneDrive do Windows. Ao encerrar, os
  arquivos marcados como "Sempre manter neste dispositivo" ficam em `~/OneDrive` (no btrfs/XFS sem ocupar espaço a
  mais). Edições, arquivos novos e renomeações feitos com o app fechado são enviados na próxima abertura. Se o arquivo
  também mudou na nuvem, as duas versões ficam (cópia de conflito). Apagou algo com o app fechado? Na abertura o
  craftDriver pergunta se apaga também no OneDrive (vai para a Lixeira) ou mantém — nada some da nuvem sem você confirmar.
  Detalhes em `docs/copias-offline.md`.

### Correções
- Pasta excluída e recriada com o mesmo nome em outro dispositivo, com uma subpasta movida da antiga para a nova: a
  subpasta não volta mais vazia.
- Encerrar com algum programa usando a pasta `~/OneDrive` (por exemplo um terminal aberto nela) não trava mais a
  desmontagem; o erro vai para o log e a desmontagem é forçada depois de alguns segundos.
- Criar arquivos na pasta ficou mais leve (menos consultas ao banco por arquivo criado).
- **Abrir na web** da conta pessoal abria uma página "File Not Found" (a Microsoft passou a informar um endereço de
  conteúdo para a pasta); agora abre o onedrive.live.com.
- Pasta movida em outro dispositivo para fora de uma pasta que foi excluída logo depois não aparece mais vazia aqui
  (o conteúdo dela sumia da montagem até uma ressincronização completa).

### Mudanças de comportamento
- `craftdriver share` (e o "Copiar link" das extensões) em conta de trabalho ou escola cria link para **pessoas da
  organização** por padrão. Para um link que qualquer pessoa abre, use `--anyone`; se o administrador não permitir, o
  craftDriver avisa em vez de trocar o tipo de link sozinho. Contas pessoais não mudam.

## [0.3.0] - 2026-11-06

### Novidades
- **Atualizações**: o craftDriver avisa quando sai uma versão nova (aviso na Visão geral com o resumo das novidades,
  notificação e item na bandeja) e **Atualizar agora** baixa o pacote certo para a sua instalação (pacman, .deb, .rpm
  ou `install-user.sh`), confere o SHA256 e instala — com a senha de administrador para pacotes do sistema, sem senha
  na instalação do usuário. "Ignorar esta versão", opção nas Configurações e `craftdriver update [--check]`.
- **Envios em paralelo** (3 por padrão), sem deixar a sincronização da nuvem esperando um envio grande.
- Pastas grandes listam mais rápido; menos idas ao chaveiro do sistema.
- Atividade com busca, filtro de erros e copiar; tema aplicado na hora; ícones vetoriais (sem depender de fonte emoji).
- Plugin do Dolphin também em .deb/.rpm (Debian 13, Ubuntu 25.04, Fedora 41); Nautilus 42 (Ubuntu 22.04) suportado.
- O tarball traz um instalador em modo usuário (sem precisar do SDK do .NET).

### Correções
- **Pacotes .deb**: faltavam `libssl3` e `ca-certificates` — num Debian 12 ou Ubuntu 24.04 mínimo o login falhava.
- **Edições que nunca subiam**: arquivo ainda aberto para escrita ao encerrar (ou numa queda) e escritas feitas depois
  de um conflito de edição com o arquivo aberto agora são enviadas.
- Arquivos criados durante uma sincronização completa ou de pasta compartilhada não são mais apagados localmente.
- `mv` sobre um nome que outro dispositivo acabou de criar não substitui mais o arquivo dele; apagar e recriar com o
  mesmo nome não vira cópia de conflito.
- A fila de envios não trava mais com um arquivo bloqueado no SharePoint (Excel Online) nem depois de descartar o aviso
  de uma pasta que não pôde ser criada; o painel mostra o que está segurando a fila.
- Arquivo renomeado ou apagado na nuvem com o gerenciador de arquivos aberto: o nome novo abre na hora (antes dava
  "arquivo não encontrado" e, depois, erro de E/S até remontar).
- Nomes com caracteres inválidos não sobrescrevem mais outro arquivo; `fstat` em arquivo apagado mas aberto funciona.
- "Entrar novamente" pela notificação com a janela na bandeja não fecha mais o app; erros inesperados vão para o log
  em vez de derrubar o processo.
- Abrir o app de novo enquanto ele espera o chaveiro não derruba mais a montagem da primeira instância.
- `systemctl --user stop`, logout e SIGTERM encerram limpo (desmonta, salva a configuração).
- Limite de banda acima de ~13 MB/s agora é respeitado; cópias de conflito desnecessárias (só metadados mudaram,
  ou conteúdo idêntico) não são mais criadas.
- **Arquivo recriado sumindo**: apagar um arquivo durante o envio (ou renomear para temporário) e recriá-lo com o mesmo
  nome não apaga mais o novo; nomes com acento em maiúsculas/minúsculas (`Ação`/`AÇÃO`) não viram cópia de conflito.
- Exclusão recusada pela nuvem (412) não deixa mais dois arquivos com o mesmo nome: a versão da nuvem volta como
  "nome (conflito - data)".
- Erro de servidor que nunca passa (5xx, 429…) num item vai para Problemas após 50 tentativas em vez de ficar oculto;
  uma exclusão em Problemas segura o envio do mesmo nome até você tentar de novo ou descartar, e o painel diz isso.
- `rm -rf` de pasta não espera mais o envio de um arquivo dela terminar; desligar/religar ou remover uma biblioteca
  com envio em andamento não envia duas vezes nem deixa exclusão órfã.
- Valores inválidos no `config.json` (lotes ou tentativas ≤ 0) são corrigidos ao carregar em vez de travar o delta.
- Mudar o limite de envio vale na hora, também para o pedaço em andamento.
- A montagem não deixa mais uma operação (open/fsync/rename) sem resposta numa corrida rara da fila interna;
  `rm -rf`, enumeração de pastas grandes e cópia de arquivos grandes fazem bem menos consultas ao banco.
- Nome de arquivo inexistente com bytes inválidos responde "não encontrado" (antes, erro de codificação).
- "Reiniciar agora" cancelado não reabre mais o app sozinho depois; disco cheio ou sem permissão em `/run` mostra o
  erro real em vez de "já está em execução"; um 2º Ctrl+C/SIGTERM durante o encerramento força a saída.
- "Sair" da bandeja pede a mesma confirmação com transferências ativas; Ctrl+S só salva na aba Configurações.
- `craftdriver quit` espera o app terminar; o `install-user.sh` não deixa mais o app fechado ao atualizar com ele
  aberto, e `--uninstall` remove as ações do Thunar.
- Pasta do craftDriver atrás de um link simbólico: menus e emblemas do Nautilus/Dolphin e o CLI funcionam.
- Dolphin: emblemas aparecem em pastas vistas antes da montagem, não lê atributos fora do craftDriver e não aborta ao
  fechar com a montagem travada. Nautilus não fica relendo para sempre um item preso em "sincronizando".
- Notificação "sessão expirada" diz onde entrar de novo quando o servidor de notificações não mostra botões.

### Mudanças de comportamento
- Uma 2ª instância `--headless` sai com código 3 (antes 0); duas configurações com a mesma pasta não montam juntas.
- O serviço systemd usa `graphical-session.target` quando a sessão o oferece (GNOME/KDE/uwsm).

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

[Não lançado]: https://github.com/Kaike-png/craftDriver/compare/v0.4.1...HEAD
[0.4.1]: https://github.com/Kaike-png/craftDriver/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/Kaike-png/craftDriver/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Kaike-png/craftDriver/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Kaike-png/craftDriver/compare/v0.1.4...v0.2.0
[0.1.4]: https://github.com/Kaike-png/craftDriver/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/Kaike-png/craftDriver/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/Kaike-png/craftDriver/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/Kaike-png/craftDriver/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/Kaike-png/craftDriver/releases/tag/v0.1.0
