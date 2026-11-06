# Pendências e melhorias — craftDriver

Rodada 1 (seções 1–11): levantamento sobre a v0.1.4 (7,5 k LOC, 40 testes) — quase tudo feito, notas "**Feito:**" em cada item.
Rodada 2 (seções 12–19): novo levantamento sobre a árvore atual (26 k LOC, 0 avisos, 238 + 118 testes verdes, nada commitado
desde `fb03c53`). Ordenado por consequência. `arquivo:linha` conferidos na data; deslocam com edições. **HIPÓTESE** = não reproduzido.
Marque `[x]` ao concluir.

**Rodada 2 concluída em 2026-11-06** (notas "**Feito (rodada 2)**" ao fim de cada seção 12–19): os 88 itens tiveram a
causa reproduzida com teste RED e foram corrigidos; 4 ficaram parciais (`[~]`): índice da `InodeTable` sem ganho no `rm -rf`
real, delta por pasta sem Graph real, i18n, publicação no AUR (precisa do segredo).

**Verificado com conta de trabalho real em 2026-11-06** (OneDrive novolare + "Setor - TI" só leitura, binário com trimming):
login por código de dispositivo (vai para `/organizations`); 58 MB em 10 s com envios em paralelo; leitura por faixas
(0,42 s/MB; 0,5 s/MB num .zip de 128 MB da biblioteca); 1 versão por gravação; restaurar e copiar versão; `mv` sobre destino
existente (DELETE com If-Match + Move com `fail` aceitos pelo SharePoint); `rm`+recriar sem conflito; `.docx` com `O_APPEND`
sem divergência; 6 arquivos Office da biblioteca com tamanho da Graph = bytes baixados; Lixeira lista (restaurar só pelo
navegador, como documentado); envio de 150 MB interrompido por SIGKILL termina inteiro, 1 versão, sem duplicata.

- [ ] **`craftdriver share` cria link "qualquer pessoa com o link" por padrão**, também em conta corporativa cujo tenant
  permite (o de "organização" só entra se a política recusar). Para quem usa conta de trabalho o padrão deveria ser
  "organização", com `--anyone` explícito para o anônimo; a interface deveria mostrar o escopo antes de criar.
- [ ] Retomada de envio não registra o offset retomado: na verificação real não deu para distinguir "retomou" de
  "recomeçou" (o resultado final está certo). Logar `Debug` com o offset de `NextExpectedOffsetAsync`.

**Ainda abertos:** instalação em modo usuário das extensões Thunar/PCManFM/Nautilus (§6); bandeja com PNG em vez de ícone
simbólico (§6); pacote Arch arm64 e app montando FUSE num ARM instalado (§9); Graph real: escrita numa biblioteca de equipe
(só leitura testada), 416 em retry, `conflictBehavior=fail` num nome criado por outro dispositivo, sessão concluída, papéis
`sp.*` (o único compartilhado da conta foi detectado como gravável — não conferido), delta por pasta (§11/§21); i18n (§18).
**Teste instável a investigar:** `StreamingTests.Hash_final_divergente_nao_vira_cache_e_descarta_os_blocos` falhou 1× em ~9
rodadas completas (só sob a suíte inteira; 30/30 isolado) e a mensagem não foi capturada. Não é a inscrição tardia na
Atividade (descartado com atraso induzido de 1,5 s). Travamento do UiTests no CI da v0.2.0: o CI agora usa
`--blame-hang-timeout`, que vai dizer qual teste é.

## 1. Bugs — perda/corrupção de dados

- [x] **`craftdriver login` sobrescrito pela instância gráfica** — `Program.cs:33-37`, `AppHost.cs:104`.
  `login` roda antes do teste de instância única, cria um segundo `AppHost` e grava `config.json`; a instância aberta
  mantém o config antigo e o regrava em `ShutdownAsync`/`TogglePause`/`SaveSettings` → a conta nova some ao "Sair".
  Correção: `login` fala com a instância via IPC (`add-account`), ou `AuthService` reconcilia contas com o chaveiro ao iniciar.
  **Feito:** `login` só autentica (sem `AppHost`) e envia `add-account` à instância aberta, que grava na thread de UI
  (`AppHost.RegisterAccount`/`RunOnMain`) e atualiza a lista; sem instância, relê o `config.json` antes de gravar.
- [x] **Renomear item somente-online para nome local-only apaga o conteúdo** — `VirtualFs.cs:521-536`.
  Enfileira `Delete` na nuvem e marca `Dirty` sem baixar; `Open` (l. 205) cria arquivo vazio. Vale para pastas inteiras.
  Correção: baixar a subárvore antes (como l. 463-467), ou recusar com `EPERM`, ou só parar de sincronizar sem excluir.
  **Feito:** `Rename` reserva e baixa a subárvore antes de mexer no banco; se algum download falhar (ou o cache não tiver
  tudo sob o lock) retorna `EIO` sem alterar nada.
- [x] **`cp -p`/`cp -a`/`install -p` perdem o mtime** — `VirtualFs.cs:256` + `SyncEngine.cs:920-923`.
  `CommitWrite` sobrescreve mtime no `Release`; o `SetTime` enfileirado antes roda com `ItemId == null` e é descartado.
  Correção: `Handle` guarda instante da última escrita + `MTimeOverride` do `Utimens`; `CommitWrite` só atualiza se não houver override posterior.
  **Feito:** `utimens` com o arquivo aberto para escrita grava um override por item (escrita/truncate posteriores o
  descartam) que o `CommitWrite` usa; `utimens` em item sem id remoto não enfileira `SetTime` (o envio leva a data) e,
  se a data mudar durante o envio, o upload enfileira `SetTime` ao terminar.
- [x] **`hard_remove` sem `nullpath_ok`** — `FuseHost.cs:367`.
  I/O em fd de arquivo já apagado falha com EIO (`exec 3<>f; rm f; echo x >&3`). Quebra `tmpfile()`, journals SQLite, editores.
  Correção: setar `nullpath_ok` no `Init` (offset varia por versão da libfuse) e tratar `path == null` nos handlers com `fi`
  usando `fi->fh`. Cuidado: `Resolve("")` devolve a raiz virtual.
  **Feito / diagnóstico corrigido:** a libfuse já passava `path == NULL` para nó apagado mesmo sem `nullpath_ok`; a
  falha real era nossa — `Truncate` buscava o item no banco (já removido → `ENOENT` no `ftruncate`) e `getattr`/`utimens`
  resolviam `""` para a raiz virtual. Agora `getattr`/`truncate`/`utimens` com `fi` usam o handle (inclusive de item
  apagado), `opendir` guarda handle e `readdir` lista por ele, e `nullpath_ok` foi ligado (offset 96, igual desde a 3.0).
- [x] **Hash divergente no download só gera Debug** — `SyncEngine.cs:176-180`. Arquivo truncado é aceito como atual.
  Correção: descartar o temporário e refazer (até N vezes); registrar erro no item.
  **Feito:** hash ou tamanho divergente descarta e refaz (`SyncEngine.DownloadAttempts` = 3); depois disso
  `ContentIntegrityException` (erro na atividade, `EIO` no `open`, `LastError` no download de fixados). Tamanho diferente
  só é aceito para documentos Office fora do OneDrive pessoal (SharePoint injeta metadados). `GraphClient` também
  rejeita corpo menor que o `Content-Length`.
- [x] **Erro permanente descarta a operação** — `SyncEngine.cs:839`. Item fica `Dirty` para sempre sem nova tentativa nem UI.
  Correção: coluna `last_error` em `items` + aba/lista "Problemas" + botão "tentar de novo".
  **Feito (tabela em vez de coluna, para cobrir exclusões de itens que já não existem):** a operação vai para
  `failed_ops`; aba "Problemas (N)" com "Tentar de novo"/"Descartar aviso"/"Tentar tudo de novo", `craftdriver status`
  lista e `craftdriver retry` devolve tudo à fila; status geral fica "Há problemas"; sucesso posterior limpa a falha.
- [x] **Cache ausente → `Dirty=false` silencioso / `Open` cria vazio** — `SyncEngine.cs:946-952`, `VirtualFs.cs:205`.
  HIPÓTESE de corrida: `EnforceCacheLimit` checa `IsOpen` fora do lock (l. 274) e o handle só é registrado após o download
  (`VirtualFs.cs:193→208`). Correção: `ENOENT`/`EIO` em vez de vazio; registrar handle (ou pin temporário) antes do download.
  **Feito:** corrida reproduzida em teste. `Open` reserva o item antes do download e abre o arquivo sob `Db.Lock`;
  `EnforceCacheLimit` confere `IsOpen` dentro do lock. Cache ausente → `EIO` + erro (nunca cria vazio); no envio, vira
  falha em "Problemas" e item com id remoto volta à versão da nuvem.

## 2. Bugs — comportamento errado

- [x] `chmod`/`chown` retornam 0 sem fazer nada — `FuseHost.cs:167,170`. Guardar bit de execução em coluna `mode` no SQLite.
  **Feito:** coluna `items.mode` (só local; o OneDrive não guarda permissões), também no `create`/`mkdir` e preservada no
  salvamento atômico; `chown` aceita só os próprios ids e devolve `EPERM` para os demais.
- [x] Offline e processo bloqueado viram o mesmo `EIO` — `VirtualFs.cs:190,199`. Usar `EHOSTUNREACH`/`EACCES` (já declarados, l. 10-12).
  **Feito:** bloqueado → `EACCES`; sem conexão → `EHOSTUNREACH`; disco cheio → `ENOSPC`; sessão expirada → `EACCES` (`Errors.ErrnoForDownload`).
- [x] Sessão de upload não persiste `uploadUrl` nem consulta `nextExpectedRanges` — `GraphClient.cs:218-258`.
  Reinício recomeça do zero; HIPÓTESE: retry de chunk após 5xx pode receber 416.
  **Feito:** `uploadUrl` + cópia congelada + geração ficam na operação (`ops.upload_*`, `IUploadSessionStore`); na retomada
  consulta `nextExpectedRanges`; 416 num reenvio consulta a sessão e continua; sessão expirada (404) recomeça; só erro
  permanente apaga a sessão. O 416 foi reproduzido contra servidor HTTP falso, não contra a Graph real.
- [x] Download sem `Range` (sem retomada) — `GraphClient.cs:171-189`.
  **Feito:** parcial por item+cTag em `cache/content/.partial`, retomado com `Range` na hora (queda de conexão) e em
  tentativas seguintes/reinícios; 416 ou resposta 200 recomeçam do zero.
- [x] `PinnedNeedingDownload` tem `LIMIT 500` e o loop espera 5 min após o lote — `SyncEngine.cs:1061-1078`, `StateDb.cs:284`.
  **Feito:** lote cheio sem falhas emenda o próximo na hora; com falha espera (evita girar em item que sempre falha).
- [x] `IOException` local (disco cheio) classificada como "sem conexão" — `SyncEngine.cs:479`.
  **Feito:** `Errors.IsLocalDiskError` (errno no `HResult`; `HttpIOException`/socket contam como rede) → erro "disco local".
- [x] Config inválido → defaults silenciosos; próxima `Save()` sobrescreve — `AppConfig.cs:170-174`. Avisar na UI.
  **Feito:** `AppConfig.LoadWarning` na barra de mensagens, na atividade e no terminal; cópia com data/hora
  (`config.json.corrompido-AAAAMMDD-HHMMSS`, não sobrescreve as anteriores); os padrões são gravados já no início.
- [x] Cabeçalho manda usar "Entrar novamente" e o botão não existe — `MainViewModel.cs:186` vs `MainWindow.axaml:125`.
  `AppHost.ReauthenticateAsync` (l. 170) implementado e nunca chamado.
  **Feito:** botão "Entrar novamente" no cartão da conta com sessão expirada; abre o login já no tipo certo e avisa se o
  usuário entrou com outra conta. `ReauthenticateAsync` removido.
- [x] `Notify` com `WaitForExit(3000)` síncrono dentro de `MassDeleteDetected` (thread do motor) — `DesktopIntegration.cs:193`, `AppHost.cs:38-43`.
  **Feito:** não espera mais o `notify-send` (processo recolhido pelo evento `Exited`).
- [x] `notify-send -i` aponta para `~/.local/share/icons` (inexistente via pacote) — `DesktopIntegration.cs:190`. Usar o nome `craftdriver`.
- [x] JSON malformado no IPC fecha sem resposta → CLI diz "não está em execução" — `Ipc.cs:48-54`.
- [x] Scripts Nautilus/Nemo expandem `$NAUTILUS_SCRIPT_SELECTED_FILE_PATHS` sem aspas — `DesktopIntegration.cs:139-144`.
  **Feito:** usam `"$@"` (o Nautilus/Nemo passa a seleção como argumentos); a variável só como alternativa, com `set -f`.
  Scripts já instalados só mudam ao reinstalar a integração.
- [x] `AddLibraryWindow` dispara cargas concorrentes sem cancelamento — `AddLibraryWindow.axaml.cs:38-39,105-115`.
  **Feito:** cada carga cancela a anterior e resposta atrasada é descartada (sem teste automatizado: precisa de Graph real).
- [x] ENOSPC detectado por `Message.Contains("space")` (falha em pt-BR) — `VirtualFs.cs:287`. `HResult == 28` basta.
- [x] HIPÓTESE: drives com o mesmo `DisplayName` sanitizado → segunda inacessível — `VirtualFs.cs:74`.
  **Feito:** confirmado por leitura (`Resolve` pega a primeira). `AppConfig.Load` normaliza: sem `/`, sem vazio/`.`/`..`,
  e únicos (" (2)") — com aviso.
- [x] `Unmount()` lê `_fuse` sem lock; HIPÓTESE de use-after-free se o loop já saiu — `FuseHost.cs:512`.
  **Feito:** `MountLock` entre `Unmount` (`fuse_exit`) e o fim do loop (zera `_fuse` antes do `fuse_destroy`). Corrida não reproduzida.
- [x] `ListXattr` em caminho inexistente retorna 0 em vez de `ENOENT` — `FuseHost.cs:269`.
- [x] `_openCount` nunca encolhe (deixa entrada em 0) — `VirtualFs.cs:365`. (resolvido junto com a reserva do `Open`)
- [x] Resíduos do nome antigo: `install.sh:2`, env `ONEDRIVE_LINUX_NO_NOTIFY` (`DesktopIntegration.cs:183`), prefixo `odl-` em
  `ScreenTests.cs`, comentário em `AppConfig.cs:118`, `~/.net/OneDriveLinux.App` órfão.
  **Feito:** variável agora é `CRAFTDRIVER_NO_NOTIFY` (a antiga não vale mais); `ODL_UI_OUT` → `CRAFTDRIVER_UI_OUT`;
  `~/.net/OneDriveLinux.App` é apagada ao abrir o app. Mantidos de propósito: esquema do chaveiro e migração `onedrive-linux`.

## 3. Desempenho

- [x] `Resolve` = (profundidade+1) SELECTs sob `Db.Lock` por operação; `getattr`+`getxattr` resolvem 2× — `VirtualFs.cs:76-80`.
  Cache path→lid invalidado em rename/delete/delta, ou resolver por lid guardado em `fi->fh`.
  **Feito:** cache caminho→lid (até 4096) que relê o item do banco a cada acerto; invalidado por `StateDb.ItemsVersion`
  (insert/update/delete/rollback). 100 resoluções de profundidade 5: 500 → 100 SELECTs. Conservador: qualquer `Update`
  em `items` limpa o cache, então o ganho é em rajadas de leitura (`ls -l`, `find`), não durante escritas.
- [x] `ContentCache.PathFor` recalcula SHA1 e `CreateDirectory` em toda chamada; `AttrOf` chama 2× por entrada de `readdir` — `Support.cs:154-162`.
  **Feito:** chave e diretório da drive calculados uma vez (1000 chamadas: 958 KB → 245 KB alocados).
- [x] Download síncrono ocupa thread do loop FUSE; 16 `open()` congelam o mount — `VirtualFs.cs:193`.
  **Feito:** `FuseHost` migrado para a API de baixo nível da libfuse (`fuse_session_*` + `fuse_reply_*`). `open`,
  truncate, `rename` e `fsync` respondem de threads próprias (`SlowOps`); o loop fica livre. Sonda
  (`tools/FuseSaturationProbe`): `stat` com 16 opens presos 2,125 s → 0,0014 s. Inodes em `InodeTable`
  (lookup/forget, subárvore no rename, inode solto no unlink com fd aberto). Testes: `NonBlockingFuseTests` (falhava
  antes), `Inodes_diretorio_grande_e_renomeacoes`; suíte FUSE inteira verde. Detalhes em `docs/desempenho-fuse.md`.
- [x] `mv` de arquivo já sincronizado baixa e sobe de novo — `VirtualFs.cs:463-467,491-509`. Usar `MoveAsync` server-side quando `IsContentCurrent`.
  **Feito:** origem sincronizada e sem alterações (inclusive somente-online) → exclusão do destino + Move da origem,
  sem download/upload (o histórico que fica é o da origem; o destino vai para a Lixeira). Origem nova/alterada
  (salvamento atômico de editores) continua virando nova versão do destino.
- [x] `Cache.TotalBytes()` percorre o cache inteiro a cada 5 s na thread de UI — `MainViewModel.cs:272`.
  **Feito:** `ContentCache.TotalBytesAsync` fora da UI, no máximo uma varredura a cada 30 s, sem sobreposição.
- [x] Dois timers de 1 s (`MainViewModel.cs:157`, `App.axaml.cs:46-48`) consultando o DB; `Activity.Clear()`+200 adds por entrada nova (l. 218-233).
  Usar o evento `ActivityLog.Changed` (existe, não usado) em vez de polling.
  **Feito:** um único timer (a bandeja usa o mesmo snapshot via `Refreshed`); atividade por `ActivityLog.Changed`,
  agrupada e incremental; o ViewModel é `IDisposable` e solta assinaturas e timer.
- [x] Página inteira do delta em transação sob `Db.Lock` bloqueia `getattr` — `SyncEngine.cs:574-595`.
  **Feito:** transações de `DriveSync.DeltaBatchSize` (32) itens, liberando o lock entre elas; `_mutate` e o deltaLink só
  no fim continuam iguais. Junto, corrigido bug: as passagens paravam em 4 e uma árvore com mais níveis chegando
  filhos-antes-dos-pais perdia o resto (teste `DeltaBatchTests`). O `Sweep` e exclusões grandes ainda são uma transação.
- [x] `IsEffectivelyPinned` = 1 SELECT por ancestral, por item cacheado — `StateDb.cs:270-275`.
  **Feito:** uma CTE recursiva (32 → 1 SELECT), resistente a ciclos.
- [x] Upload simples faz PATCH extra para a data (2 round-trips por arquivo pequeno); delta sem `$select` — `GraphClient.cs:213`.
  **Feito:** `$select` no delta com os 15 campos usados. **Revisto na verificação com o OneDrive real:** o PATCH da
  data cria uma SEGUNDA versão a cada envio (3 edições de um .txt = 6 versões; o arquivo grande, por sessão, tinha
  1). Agora todo arquivo com conteúdo sobe por sessão, com a data no `createUploadSession` (POST + PUT: as mesmas
  2 chamadas); só o vazio usa PUT + PATCH. Conferido no real: 1 versão por envio e data de 2020 preservada.
- [ ] Uploads/downloads seriais por drive; sem paralelismo nem limite de banda.
  **Feito:** downloads paralelos por drive (`DownloadConcurrency` = 4, sob demanda e fixados juntos) e limite de banda
  por drive e direção (`DownloadBytesPerSecond`/`UploadBytesPerSecond` no `config.json`, ≤ 0 = sem limite; sem tela).
  **Pendente:** uploads paralelos — a fila só tem `PeekOp`, sem reserva atômica nem dependências pai/filho.

## 4. Remover

- [x] `StateDb.AllOps()` (l. 428) e `AllDirty()` (l. 292): sem chamadores. (o único uso de `AllOps`, num teste, virou `PeekOp`)
- [x] `Errno.EBUSY/EROFS/ENOSYS` (`VirtualFs.cs:10-12`): usar ou remover. Removidos (`EHOSTUNREACH`/`EACCES` passaram a ser usados).
- [x] `FileAttr.CTimeMs` calculado e descartado (`FuseHost.cs:72,101`); hoje `ctime == mtime`.
  **Removido de `FileAttr`** (o ctime do stat continua = mtime). A data de criação da nuvem (`Item.CTime`) não serve de
  ctime POSIX ("mudança de status"): um ctime anterior ao mtime confundiria backups/rsync. `Item.CTime` fica no banco.
- [x] Launcher/servicemenu duplicados em `DesktopIntegration.cs:43-55,106-129` vs `packaging/share/**` (já divergem). Uma fonte só.
  **Feito:** `packaging/share/**` embutido no App (`EmbeddedResource`); modo usuário e autostart só trocam o `Exec=`.
  Passaram a ganhar `GenericName[en]`, `Comment[en]` e `Keywords` que só o pacote tinha (`DesktopFilesTests`).
- [x] `.SRCINFO` à mão em `build.sh:57-86` → `makepkg --printsrcinfo`.
  **Feito:** `packaging/arch/srcinfo.sh` (makepkg local, ou contêiner `archlinux` sem root — no CI). Saída conferida
  idêntica à do texto antigo nos dois caminhos; PKGBUILD inválido faz o build falhar.
- [x] `coverlet.collector` sem passo de cobertura no CI.
  **Feito:** `--collect:"XPlat Code Coverage"` nos testes; resumo do ReportGenerator 5.5.12 no Job Summary e HTML
  como artefato `cobertura`. `coverlet.collector` também no UiTests (cobre o App). Linha de base local: 72,4% de linhas,
  57,4% de ramos.

## 5. Débito estrutural

1. ~~Config em memória sem reconciliação (gera bug 1). Adicionar `SchemaVersion` e `chmod 600` em `config.json`.~~
   **Feito:** a reconciliação veio com o bug 1 (o `login` entrega a conta por IPC à instância, que é dona do arquivo).
   `SchemaVersion` (1) é gravado; arquivo de versão **mais nova** é carregado mas não é sobrescrito (`ReadOnly`, com
   aviso; o `login` com o app fechado falha em vez de dizer que salvou). `Save` grava 0600 desde o temporário e corrige
   arquivos 0644 antigos.
2. ~~Conexão SQLite única + lock global entre FUSE, motor e UI (gera 5 itens de desempenho). WAL já ligado → conexão de leitura para a UI é viável.~~
   **Feito:** conexão `Mode=ReadOnly` para `PendingOps`, `FailureCount`, `Failures`, `Counts` e `AllPinned` (status,
   interface, IPC): com o lock do motor preso por 1,5 s, essas leituras levavam 1,5 s e agora não esperam. Elas veem só
   dados confirmados (nenhum chamador lê dentro de transação; `RetryFailure` continua na conexão principal). As escritas
   e o FUSE seguem na conexão única.
3. ~~Sem estado "falhou" por item (gera bug de op descartada e a UX de erros).~~ Feito na seção 1 (`failed_ops` + aba Problemas).
4. Handlers FUSE síncronos (bloqueiam o loop; `Ctrl-C` num `cat` não cancela — `VirtualFs.cs:193` usa o token global).
   **Feito o cancelamento:** opção `intr` da libfuse ligada (sem ela a API de alto nível descarta o `FUSE_INTERRUPT`) e
   o download no handler consulta `fuse_interrupted()` a cada 100 ms; Ctrl-C cancela e o `open` devolve `EINTR` (o
   parcial fica para retomar). **Feito também o resto:** handlers que podem ir à rede respondem de forma assíncrona
   (API de baixo nível; ver seção 3). Na API nova o Ctrl-C usa `fuse_req_interrupted` por requisição, sem `intr`/SIGUSR1.

## 6. Refinar funcionalidades existentes

- [x] ~~Botão "Entrar novamente" por conta~~ (feito na seção 2) + notificação ao expirar sessão.
  **Feito:** `SessionExpiryTracker` avisa uma vez por conta por expiração, com ação "Entrar novamente" que abre o login
  daquela conta (`AppHost.ReauthRequested` → `MainViewModel`). Sem janela (`--headless`) indica `craftdriver login`.
- [x] Banner de exclusão em massa: listar o que foi apagado antes de confirmar.
  **Feito:** `DriveSync.HeldDeletes()` remonta os caminhos das próprias exclusões retidas (pasta apagada aparece uma
  vez, com o total dentro; coluna nova `ops.is_dir`); o banner mostra os 20 primeiros num "Ver o que foi apagado".
- [x] Transferências: velocidade/ETA/cancelar.
  **Feito:** `Transfer.BytesPerSecond`/`Remaining` (janela de 5 s; o salto de uma retomada não conta) e botão Cancelar.
  Download cancelado: o `open` devolve `ECANCELED` e o parcial fica. Envio cancelado: vai para Problemas (a fila só
  avança pela cabeça; adiar travaria a drive), com "Tentar de novo"; as alterações locais continuam.
- [x] `setxattr user.onedrive.pinned` para fixar/liberar.
  **Feito:** `1`/`0`/`true`/`false`; `removexattr` = liberar; `XATTR_CREATE` → `EEXIST`, valor inválido → `EINVAL`.
  Leitura mostra a marca do próprio item (o status efetivo segue em `user.onedrive.status`). Teste com montagem real.
- [x] Emblemas no Dolphin (`KOverlayIconPlugin`) / nautilus-python lendo `user.onedrive.status`.
  **Feito:** `extensions/dolphin` (C++/KF6; overlay + ações; ctest, e conferido num Dolphin 26.08 real) e
  `extensions/nautilus/craftdriver.py` (testado só com `gi` falso — Nautilus não instalado). O plugin do Dolphin é o
  pacote AUR separado `craftdriver-dolphin` (o CI Ubuntu não tem KF6; job `dolphin-plugin` em contêiner archlinux).
- [x] Lista/notificação de conflitos na UI.
  **Feito:** tabela `conflicts`, aba "Conflitos (N)" (abrir pasta / dispensar, sem apagar a cópia) e notificação
  limitada a uma a cada 30 s (o resto fica na aba).
- [x] Autostart: detectar Hyprland/Sway e oferecer unit `systemd --user` com `--headless` + `Restart=on-failure`.
  **Feito:** opção nas Configurações, visível só em Hyprland/Sway, com o aviso de que `--headless` não tem janela nem
  bandeja (alternativa `exec-once`). Ativar um modo remove o outro.
- [x] Servicemenu com `MimeType=all/all` aparece em qualquer arquivo — restringir ao mount.
  **Feito com o plugin:** o plugin de ações do Dolphin só mostra o menu quando todos os itens têm o xattr, e o pacote
  dele esconde o servicemenu (`/etc/xdg/kservicemenurc`; ações renomeadas para `craftdriver_*`). **Sem o plugin** o
  servicemenu continua aparecendo em qualquer arquivo (o formato não permite filtrar por caminho).
- [x] Thunar (`uca.xml`), PCManFM, extensão Nautilus real (não "Scripts").
  **Feito para os pacotes** (`.deb`/`.rpm`/tarball/`craftdriver-bin`): PCManFM com `ShowIfTrue` pelo xattr; Thunar com
  instalador `/usr/share/craftdriver/thunar/install-uca.py` (Thunar não filtra por caminho). Testados só sem os
  programas. **Pendente:** instalação em modo usuário (sem pacote) desses três.
- [x] `.desktop`: `Actions=` (Abrir pasta/Sincronizar/Pausar), AppStream metainfo, SVG em `hicolor/scalable`, ícone simbólico de bandeja.
  **Feito:** ações com o novo `craftdriver open`; metainfo `io.github.kaike_png.craftdriver` (validado com
  `appstreamcli`); SVGs scalable e `craftdriver-symbolic` nos pacotes. **Não feito:** a bandeja ainda usa os PNGs de
  status (o `TrayIcon` do Avalonia não aceita nome de ícone do tema).
- [x] Notificações via D-Bus `org.freedesktop.Notifications` com ações e hint `desktop-entry`.
  **Feito:** `DesktopNotifier` (Tmds.DBus.Protocol, a mesma versão que já vinha pelo Avalonia), ações sem bloquear o
  motor, fallback `notify-send` (sem botões). Testado contra servidor falso num dbus-daemon privado.

## 7. Features novas

- [x] Leitura por faixas (streaming): `Range` + arquivo esparso + bitmap de blocos; `fi->direct_io` enquanto incompleto. **Feito:** arquivos ≥ `StreamingThresholdMB` (8 MB) abrem na hora para leitura; blocos de 4 MiB por `DownloadRangeAsync`, esparso + bitmap persistido, segundo plano na leitura sequencial, hash do arquivo inteiro antes de virar cache, `direct_io` e read com rede em `SlowOps`. Provado por `StreamingTests`/`StreamingFuseTests` (RED antes; 12 mutações pegas). Desenho em `docs/streaming.md`.
- [x] Retomada de upload/download após reinício (persistir `uploadUrl` em `ops`; `Range` no download). (feito na seção 2)
- [x] Sincronização seletiva (flag "ignorar" por pasta em `ApplyRemote`).
  **Feito:** tabela `excluded`; pasta excluída (e a subárvore) some de `Resolve`/`ReadDir`, o conteúdo baixado sai do
  cache, a fixação de dentro é desfeita e fixar a drive inteira não a baixa. Metadados continuam chegando pelo delta
  (reincluir é imediato). Recusa raiz, arquivo e pasta com alterações não enviadas. Janela "Escolher pastas…" por
  drive e `craftdriver exclude|include`. Provas: `SelectiveSyncTests`, `Escolher_pastas_exclui_reinclui_e_mostra_recusa`.
- [x] "Compartilhados comigo" e atalhos (`IsRemoteShortcut` já detectado e descartado em `SyncEngine.cs:635`; `/me/drive/sharedWithMe`). **Feito:** atalho vira sub-drive onde foi colocado e cada conta ganha a pasta irmã "<drive> - Compartilhados comigo"; listagem por `children` (sem delta fora da raiz), leitura e escrita na drive do dono, somente leitura = 0444/0555 + EACCES sem enfileirar; provado em `SharedTests` (motor + FUSE real) e `SharedGraphProtocolTests`, com mutações. Desenho, limites (sharedWithMe obsoleta pela Microsoft) e fontes em `docs/compartilhados.md`.
- [x] Link de compartilhamento pelo menu de contexto (`createLink` + IPC `share` + `wl-copy`). **Feito:** `craftdriver share [--edit] [--notify]` → `ItemActions.CreateShareLinkAsync` (anonymous; em conta corporativa recusada por política 400/403/405 → organization, avisado) → `ClipboardService` (wl-copy / xclip / xsel) + notificação "Link copiado" com "Abrir no navegador" (enviada pela instância). Menus: servicemenu + kservicemenurc, plugin Dolphin, Nautilus, PCManFM, Thunar. Provas: `ItemActionsTests`, `GraphActionsProtocolTests` (URL/corpo), `ShareVersionsHostTests` (clipboard/notify injetados), ctest do Dolphin; mutações RED. Não verificado contra a Graph real.
- [x] Versões anteriores / restaurar da Lixeira (`/items/{id}/versions`). **Feito:** janela "Versões" (`craftdriver versions`, menus) com Restaurar (`restoreVersion`; atualiza cTag/eTag na hora e descarta o cache — leitura seguinte baixa a versão restaurada; fixado rebaixa) e Baixar cópia (`versions/{id}/content` → `nome (versão AAAA-MM-DD HHMM).ext` ao lado, O_EXCL, enviado como arquivo novo). Restaurar com alterações locais não enviadas (`Dirty`/upload pendente) é **recusado** com mensagem. Lixeira: tabela `trash` (exclusões enviadas daqui e vistas no delta); restaurar pela API só no OneDrive pessoal (`POST /me/drive/items/{id}/restore`, v1.0 só pessoal); corporativo/SharePoint (`/sites/{id}/recycleBin` é só beta) → abre `…/_layouts/15/RecycleBin.aspx` no navegador. Janela "Lixeira" (bandeja/`craftdriver trash`). Não verificado contra a Graph real (URL da Lixeira pessoal `onedrive.live.com/?v=recyclebin` incluída).
- [x] Interrupção de download (`fuse_interrupted()` + CTS por open). Feito na seção 5 (hoje `fuse_req_interrupted`).
- [x] `st_ino = lid` + `use_ino` (inode estável entre montagens).
  **Feito:** na API de baixo nível o inode é o próprio número entregue ao kernel: `lid + 1` (lid é AUTOINCREMENT,
  nunca reaproveitado; 1 = raiz). Nós virtuais (ex. "Compartilhados comigo") usam números ≥ 2^48 só da montagem.
  Prova: `Inode_e_o_mesmo_entre_montagens_e_depois_de_renomear` (FUSE real; falhava com 4 × 5).
- [x] Limite de banda / paralelismo configurável.
  **Feito:** seção "Rede" nas Configurações (downloads simultâneos, MB/s de download e de envio; por drive) aplicada
  ao salvar sem reiniciar: `AdjustableLimiter` redimensionável e banda mutável (vale do próximo pedaço em diante).
  Provas: `TransferLimitTests`, `Configuracoes_de_rede_valem_sem_reiniciar`.
- [x] `copy_file_range` local (ambos os lados no cache).
  **Feito:** handler no FUSE (em SlowOps) copiando pelo cache em blocos de 1 MB via `Read`/`Write` (destino marcado
  como alterado e enviado normalmente). Prova: `Copia_dentro_da_montagem_usa_copy_file_range` (FUSE real; sem o
  handler o kernel faz read/write de 128 KB e o teste falha).

## 8. UI/UX

- [x] Confirmação em: Desvincular conta (descarta pendências, `AppHost.cs:196-197`), Parar de sincronizar, Liberar espaço de tudo, Salvar com mount alterado.
  **Feito:** `ConfirmWindow` (Esc = não, Enter = sim; em ação perigosa o foco começa em Cancelar e o botão não é azul) via
  `MainViewModel.ConfirmRequested`. O texto diz o que some e quantas alterações não enviadas serão descartadas; liberar
  espaço mostra quantos arquivos. Sem quem confirme (sem janela), a ação não acontece. Provas: `UxTests` (4 testes).
- [x] Estados vazios em Atividade, drives e fixados.
  **Feito:** textos explicando o próximo passo em Atividade, Contas, OneDrive e SharePoint e Arquivos sob demanda
  (`HasActivity`/`HasDrives`/`HasPinned`). Prova: `Listas_vazias_explicam_o_que_fazer`.
- [x] Barra `Message`: limpar/fechar, estilo distinto para erro; `ActivityVm.IsError` não é usado no template (`MainWindow.axaml:96-101`).
  **Feito:** `ShowError` liga `MessageIsError` (fundo/borda de erro; trocar a mensagem zera), botão ✕ e texto
  selecionável; "Reiniciar agora" quando há configuração pendente. Atividade com erro usa a classe `error`.
  Provas: `Mensagem_de_erro_tem_estilo_proprio_e_pode_ser_fechada`, `Atividade_com_erro_aparece_destacada`.
- [x] Nenhum `KeyBinding`/`HotKey`/`IsCancel`/`AutomationProperties` no App (Esc não fecha diálogos; sem acessibilidade).
  **Feito:** F5/Ctrl+R sincroniza, Ctrl+, abre Configurações, Ctrl+S salva, Ctrl+Q sai; `IsCancel` em Escolher pastas,
  Adicionar biblioteca, Versões, Lixeira e confirmação (o Login fica com o patch da "casca"). Campos e caixas sem texto
  têm `AutomationProperties.Name`/`LabeledBy`; ícones decorativos ficam fora da árvore. Provas: `Atalhos_da_janela_principal`,
  `Dialogos_fecham_com_esc`, `Controles_sem_texto_tem_nome_acessivel` (percorre todas as abas). Leitor de tela real (Orca) não testado.
- [x] Cores fixas (`#C42B1C`, `IndianRed`) fora do tema; emojis em botões; sem opção manual claro/escuro (HIPÓTESE: sem portal no Hyprland fica claro).
  **Feito:** `Styles/CraftDriverStyles.axaml` com `CdErrorBrush` claro/escuro (classes `error`, `danger`); botões sem
  emoji; "Aparência: Seguir o sistema / Claro / Escuro" (`AppConfig.Theme`, `ThemeService`). A hipótese não se confirmou
  nesta máquina: o portal do Hyprland responde `org.freedesktop.appearance color-scheme = 1` (escuro); a opção manual
  cobre sessões sem portal. Prova: `Tema_claro_escuro_ou_do_sistema` + PNG `13-tema-escuro`.
- [x] Lembrar tamanho/posição da janela; aviso ao fechar para a bandeja; detectar ausência de host SNI.
  **Feito:** tamanho/posição/maximizado em `AppConfig` (posição só volta se a barra de título cair numa tela atual; o
  compositor Wayland pode ignorá-la). `TrayHostProbe` consulta `IsStatusNotifierHostRegistered` ao fechar: com bandeja,
  some e avisa uma vez ("continua em execução"); sem bandeja, pergunta Encerrar/Minimizar/Cancelar em vez de sumir, e o
  início minimizado abre a janela. Provas: `ShellTests` (dbus privado com e sem watcher falso). Hyprland real não testado.
- [x] `MountPoint` com seletor de pasta + validação; `CacheDir` editável; `ClientId` alterado → oferecer reiniciar.
  **Feito:** botões "Escolher…"; montagem recusada se relativa, arquivo, pasta do sistema/home, não vazia ou cruzada com o
  cache, e troca pede confirmação. Cache editável (vale ao reiniciar; recusado com alterações não enviadas, que só existem
  no cache atual; o resto é baixado de novo porque `HasContent` exige o arquivo). Client ID validado como GUID. Ambos
  oferecem "Reiniciar agora" (`AppRestart`: espera o PID antigo sair e reabre sem `--minimized`).
  Provas: 4 testes em `UxTests` e `Reiniciar_espera_o_processo_antigo_terminar_e_abre_com_os_mesmos_argumentos`.
- [x] `AppHost.CreateAsync()` bloqueia antes do Avalonia subir (`Program.cs:55`), até 60 s de chaveiro (`AuthService.cs:75`) sem feedback.
  **Feito:** o Avalonia sobe na hora e o `AppHost` é criado em segundo plano (`StartupCoordinator`); após 400 ms abre a
  `SplashWindow` "Abrindo o chaveiro do sistema…" com "Continuar sem o chaveiro" e "Sair". Erro de inicialização aparece
  na janela. `--headless`/CLI sem janela. Provas: `ShellTests` com chaveiro falso lento/travado. Pendente conhecido: uma
  segunda execução durante a espera ainda abre outra instância (o IPC só sobe com o `AppHost`).
- [x] `LoginWindow` em erro: mensagem crua do MSAL, sem "Tentar de novo".
  **Feito:** `Core/Auth/LoginErrors.cs` traduz cancelamento, consentimento do admin (AADSTS65001/90094/90095), recusa,
  conta/tenant errado, código expirado, sem rede, porta ocupada, navegador e serviço fora; detalhe técnico recolhível;
  "Tentar de novo" refaz o último fluxo; Esc fecha. Provas: `LoginErrorsTests`, `ShellTests`.
- [~] Avalonia via XWayland (só `Avalonia.X11`); HIPÓTESE: borrado em HiDPI sem `xwayland:force_zero_scaling`.
  **Mitigado (não provado):** Avalonia 12.1.5 tem backend Wayland só experimental (não adotado). O X11 lê
  `AVALONIA_SCREEN_SCALE_FACTORS`/`AVALONIA_GLOBAL_SCALE_FACTOR`, `QT_*` e `Xft.dpi` — não `GDK_SCALE` (medido aqui).
  `DisplayScaling` (antes do AppBuilder): com `force_zero_scaling = true` aplica a escala de cada monitor lida do
  Hyprland; sem ela não mexe e registra a dica no log; variáveis do usuário prevalecem; `CRAFTDRIVER_AUTO_SCALE=0`
  desliga. O borrado em si não foi observado (monitores daqui em escala 1). Ver `docs/inicio-e-janela.md`.

## 9. Pacotes, empacotamento e CI

- [x] Binário de 102 MB: adicionar `EnableCompressionInSingleFile` (~-60 %), `InvariantGlobalization` (dispensa `libicu*` em `nfpm.yaml:28`);
  testar `PublishTrimmed` (HIPÓTESE: Avalonia+MSAL).
  **Feito:** `PublishTrimmed` em pasta: 52,5 MB instalado, tar.gz 21,1 MB (antes 102,8/43,6). Compressão medida e
  rejeitada (só reduz o instalado, +0,15–0,4 s por abertura, continua extraindo). `InvariantGlobalization` rejeitado:
  números/datas/ordem em pt-BR quebram (tabela em `docs/empacotamento.md`). Trimming seguro: análise de trimming como
  erro de build em Core/App (RED: 8 usos de JSON por reflexão), trocados por `JsonSerializerContext` com formato
  idêntico (`JsonFormatTests`; mutação `WriteIndented` pega). Executado com trimming: login por código e pelo navegador
  até a resposta real de erro do endpoint de token, e `packaging/test/run.sh` nas 4 distros (FUSE, IPC, janela).
  Não verificado: login completo com conta real e chaveiro real no binário com trimming.
- [x] `IncludeNativeLibrariesForSelfExtract=true` extrai em `~/.net/<app>/<hash>/` sem limpar (59 MB + 15 MB órfãos); falha em `$HOME` só leitura.
  Alternativa: não usar single-file no pacote (já tem `/usr/lib/craftdriver`).
  **Feito:** sem single-file. Reproduzido antes: `$HOME` só leitura → rc 159; 14 MB em `~/.net` por versão. Depois:
  abre com `$HOME` só leitura e nada em `~/.net`. Pacotes instalam a pasta em `/usr/lib/craftdriver` (nfpm `tree`,
  modos normalizados 0644/0755); tarball traz `lib/` + link `craftdriver`.
- [x] Dependências ausentes em `nfpm.yaml:22-44` e `PKGBUILD.in:10-13`: `xdg-utils`, `libnotify(-bin)`, `dbus(-tools)`.
  **Feito:** levantadas pelo código (`xdg-open`, `dbus-send`, `notify-send`, `wl-copy`/`xclip`/`xsel`), nomes
  conferidos em cada distro: obrigatórias `xdg-utils` e `dbus-bin|dbus` / `dbus-tools` / `dbus`; recomendadas
  `libnotify(-bin)` e cópia para a área de transferência. Os 4 contêineres instalam com elas.
- [x] Só amd64; README menciona aarch64. AppImage viável com o tarball; Flatpak fora de escopo (FUSE).
  **Feito:** `ARCHES="x64 arm64" packaging/build.sh` gera tarball/.deb/.rpm arm64 e PKGBUILD com
  `source_x86_64`/`source_aarch64`. **Executado no GitHub Actions** (run 42106498338, executor `ubuntu-24.04-arm`):
  `CraftDriver.Tests` 234 aprovados + 4 pulados de propósito (3 exigem `direct_io`, ausente na libfuse 3.14; 1 é o
  cenário do processo filho), com FUSE real e `FuseLayoutTests` conferindo os offsets aarch64 nos cabeçalhos reais;
  `UiTests` 118/118; pacotes arm64 instalados em Ubuntu, Debian e Fedora com a janela abrindo nos três.
  O primeiro run achou um teste que só passava com `direct_io` (desmontagem com reads no mesmo arquivo), corrigido.
  **Não verificado:** pacote Arch arm64 (sem imagem) e o app instalado montando a pasta num ARM de verdade.
  AppImage não feito (o app já depende do fuse3 do sistema; o tarball cobre "qualquer distro").
- [x] CI: `ScreenTests.cs:94-137` chama endpoints reais da Microsoft (flaky); PNGs das telas não viram artefato; sem cache NuGet,
  `timeout-minutes`, `concurrency`, `dotnet format`; `smoke.sh:26` não falha sem janela; `--generate-notes` silencioso (`ci.yml:85`).
  **Feito (patch do subagente + integração):** `FakeMicrosoftLogin` no lugar da Microsoft real (prova com `unshare -rn`:
  118/118 sem rede; antes 116/117) e o teste de login agora exige a troca do código no endpoint de token; artefatos
  `telas-ui-*`/`resultados-testes-*` com `if: always()`; cache NuGet; `timeout-minutes`; `concurrency` (sem cancelar
  tags); job `checks` com `dotnet format whitespace --verify-no-changes` (`.editorconfig` do estilo atual);
  `smoke.sh` falha sem janela em 30 s; notas da release vêm de `packaging/release-notes.sh` (seção do CHANGELOG) e a
  tag sem seção falha no `checks`. actionlint 1.7.12 sem erros. Não verificado: o Actions de verdade.
- [x] `Tests` em xunit v2 (2.9.3) e `UiTests` em xunit.v3 (3.2.2) → unificar. Atualizações: `Microsoft.NET.Test.Sdk` 17.14→18.11,
  `xunit.runner.visualstudio` 3.1→4.0, `coverlet` 6.0→10.1. 4 avisos `xUnit1031`.
  **Feito:** os dois em `xunit.v3.mtp-off` 3.2.2 (o `Avalonia.Headless.XUnit` 12.1.5 quebra com a 4.x; o `mtp-off`
  mantém VSTest/`--filter`/trx/cobertura), Test.Sdk 18.11.0, runner 4.0.1, coverlet 10.1.0, versões centralizadas em
  `tests/Directory.Packages.props`. Build da solução com 0 avisos (eram 42: xUnit1031/1051, CS0618, CA1416).
- [x] `CHANGELOG.md` consolidado em vez de só `packaging/release-notes/vX.md`.
  **Feito:** `CHANGELOG.md` (0.1.0–0.1.4 + "Não lançado"); `packaging/release-notes.sh vX.Y.Z` recorta a seção e
  acrescenta a tabela de instalação, falhando sem seção; vai no tarball e em `/usr/share/doc`.

## 10. Lacunas de testes

**Feito** (todos os itens abaixo têm teste; detalhes em seguida). Eram nunca exercitados: `Fsync`, `Flush`, `StatFs`, `SetXattr`, `O_APPEND`, `pread` parcial, `mmap`,
`O_EXCL`, clash de maiúsculas, `ENAMETOOLONG`, `ENOSPC`, leitura via FUSE de online-only grande,
processo bloqueado, dois `open` concorrentes do mesmo online-only, `RENAME_NOREPLACE`, **duas drives** (`EXDEV`, raiz
virtual, nomes duplicados), `chmod`, Unicode/NFD, retomada de upload.
**Cobertos agora (`FileOpsTests`, FUSE real):** `Fsync`, `Flush` (close de fd duplicado), `StatFs` (`df` = cota da
drive), `SetXattr` alheio (`ENOTSUP`/`ENODATA`, sem baixar nem enviar; `cp -a` com xattr funciona), `O_APPEND` em
só-online, `pread` parcial, `mmap` (leitura de só-online, `MAP_SHARED` gravando, e arquivo grande por faixas) e
`O_EXCL`. **Defeitos achados e corrigidos:** (1) `fsync` com o arquivo aberto enviava o estado intermediário (2 envios;
agora só marca e grava no disco, envio no close); (2) `mmap` de arquivo grande em leitura por faixas dava `ENODEV`:
o `init` negocia `DIRECT_IO_ALLOW_MMAP` (libfuse ≥ 3.17.3) e mantém `direct_io`; sem ela (Ubuntu 24.04/Debian 12)
usa o cache de página, com erros virando `EIO` (docs/streaming.md). Testado nos dois modos. Mutações: 6 de 8 pegas; `pread` parcial e `O_EXCL` são
garantidos também pelo kernel (corte pelo tamanho / `EEXIST` no lookup), então os testes só falham se as duas camadas
quebrarem. **Testes instáveis com causa no produto:** `ChunkSize` era estático e mutável (testes o trocavam e outro
envio em paralelo lia valores diferentes → `ArgumentOutOfRangeException`): virou propriedade `init` do `GraphClient`;
o aviso "blocos descartados" saía antes do descarte: ordem invertida, com teste que confere o estado no momento do
aviso (RED antes). Suíte 6× seguidas verde.
**`NamesAndDrivesTests` (subagente):** duas drives (`mv` entre elas = `EXDEV` + cópia), raiz virtual, drives com o
mesmo nome, `RENAME_NOREPLACE`/`EXCHANGE`, maiúsculas, NFC/NFD, `ENAMETOOLONG`, `chmod`. Defeitos corrigidos:
maiúsculas fora do ASCII (`AÇÃO.txt` ao lado de `ação.txt`: o `NOCASE` do SQLite só cobre A–Z); NFC e NFD do mesmo
nome coexistindo (agora `EEXIST`); limite de 255 contado em caracteres em vez de bytes; caminho > 400 caracteres
recusado pela nuvem travava a fila da drive inteira (filhos agora falham com "pasta não pôde ser criada").
**`ConcurrencyAndResumeTests` (subagente):** só-online grande pelo shell, processo bloqueado, dois opens
concorrentes, retomada de upload (sessão, expirada, arquivo alterado), `ENOSPC` em tmpfs real (`unshare -rm`).
Defeitos corrigidos: processo bloqueado comparado pela linha de comando inteira (`cat miniaturas/thumbnail-x.jpg`
dava `EACCES`); download que não cabe deixava o parcial ocupando o disco (e o bloco por faixas no esparso: agora
`PUNCH_HOLE`); **edição durante o envio do mesmo arquivo nunca subia** (o novo pedido coalescia com a operação em
execução, que saía da fila). Decisões a revisar: maiúsculas continuam com lookup exato e `EEXIST` no clash (não abre
`a.txt` por `A.txt`); NFC×NFD iguais por escolha (a Microsoft não documenta). Não verificado: tudo contra a Graph
real; modo cache de página num Ubuntu/Debian real; coalescência em Move/SetTime em andamento.
(Cobertos desde a seção 1: `ftruncate`/`fstat` em fd apagado, `utimens` de `cp -p`/`cp -a`/`install -p`, LRU entre
download e `open`, rename para nome local-only, hash divergente, falha de download no `open`, erro permanente na fila.)
~~`FuseIntegrationTests.cs:24` passa silenciosamente sem `/dev/fuse` — usar `Skip`.~~ Feito: `RealFuse.Require()` (`Assert.SkipUnless`); com `CRAFTDRIVER_TEST_NO_FUSE=1` aparecem 12 ignorados.

## 11. Não verificado

- [x] Layout de `struct stat`/`fuse_config` em aarch64 e libfuse < 3.17 (offsets x86_64 conferidos com libfuse 3.18.3 via `offsetof`).
  **Feito:** `tools/fuse-layout/probe.c` mede `stat`, `statvfs`, `fuse_entry_param`, `fuse_file_info` e os índices de
  `fuse_lowlevel_ops` por `offsetof` (só compila, serve para cruzado); `FuseLayout.cs` guarda os valores e
  `FuseLayoutTests` confere contra x86_64 3.18, x86_64 3.14 e aarch64 3.14 (Debian 12) e contra os cabeçalhos da
  máquina em que roda. **Defeitos corrigidos:** no aarch64 a `stat` tem 128 bytes (não 144), então
  `attr_timeout`/`entry_timeout` eram gravados 16 bytes fora do lugar; na libfuse 3.14 a `fuse_file_info` tem 40
  bytes e o open copiava 64 (lia além do fim). `fuse_config` não é mais usado (API low-level, `fuse_loop_cfg_*`).
  Suíte inteira no Ubuntu 24.04 (libfuse 3.14, usuário comum): 229 aprovados, 3 pulados por exigirem `direct_io`
  (garantias que a 3.14 não dá; docs/streaming.md). **Conferido em aarch64 real** no CI ARM (teste contra os
  cabeçalhos da máquina, passou).
- [x] Corridas LRU×Open e `Unmount` use-after-free (raciocínio, não reproduzidas).
  **Feito:** LRU×Open **existia**: o Open decidia "cache em dia" pela cópia do item lida antes da reserva; um despejo
  que conferiu `IsOpen` antes apagava o arquivo e o open dava `EIO`. Reproduzida com costura
  (`VirtualFs.OpenStepForTests`), corrigida relendo o item depois do `Reserve`. Mesmo caso no `truncate` por caminho
  (sem fd: não reservava; dava `-EHOSTUNREACH`), corrigido. Estresse LRU × 3 threads de open/read/release. `Unmount`:
  não reproduz (lock + `_session` zerada antes do destroy + espera de `_inflight`); `UnmountRaceTests` roda num
  processo filho 6 montagens com opens/reads presos na rede e 3 jeitos de desmontar; tirar a espera de `_inflight`
  dá SIGSEGV (139) e o teste pega. Não pego: tirar só o lock do `Unmount` (janela de nanossegundos).
- [~] Comportamento com a Graph real (probes usam `FakeGraph`): 416 em retry de chunk; tamanho divergente de Office no SharePoint × `O_APPEND`.
  **Parcial:** verificado com uma conta pessoal real, binário publicado com trimming e instância isolada
  (`CRAFTDRIVER_HOME`, item próprio no chaveiro), tudo numa pasta de teste apagada no fim: login pelo navegador;
  envio de 40 MB em partes (15 s); `free` + leitura de 1 MB no meio por faixas (open 1 ms, pread 1,2 s, 4,1 MB
  em disco no esparso de 40 MB) e arquivo inteiro com sha256 igual ao original; versões (listar, restaurar,
  `--copy`); Lixeira (excluir, `trash restore` com a data preservada); exclusão de pasta coberta. **Achado:** versões
  duplicadas pelo PATCH da data (seção 3, corrigido). **Falta:** 416 em retry de chunk, SharePoint/conta de
  trabalho (inclusive `/common` × `/organizations` no código de dispositivo), Office × `O_APPEND`, link de
  compartilhamento (não criado de propósito: link público), retomada de envio interrompido.
- ~~`PublishTrimmed` com Avalonia/MSAL~~ (ver seção 9); ~~rendering em XWayland/Hyprland; detecção de tema via portal.~~
  Conferido no Hyprland (2 monitores 1920×1080, escala 1): janela XWayland nítida, tema escuro vindo do portal em
  "Seguir o sistema", ícone registrado no StatusNotifierWatcher enquanto roda e removido ao sair. Escala fracionária
  não conferida (os monitores daqui são escala 1).
- [x] **Achado na verificação:** instância com `CRAFTDRIVER_HOME` (testes, desenvolvimento) usava o MESMO item do
  chaveiro da instalação normal (o chaveiro é por sessão, não por HOME): um login de teste sobrescrevia os tokens.
  **Feito:** `AuthService.KeyringIdentity` usa outro atributo `Product` (hash do caminho) e o caminho no rótulo;
  sem `CRAFTDRIVER_HOME` nada muda (tokens salvos continuam valendo). `KeyringIdentityTests`.
- ~~`StartupWMClass=CraftDriver.App` vs `WM_CLASS` real do Avalonia.~~ Conferido: `("craftdriver", "CraftDriver.App")` no Xvfb, a classe bate.

## Ordem sugerida (rodada 1 — concluída)

1. ~~Seção 1~~ 2. ~~"Entrar novamente" + estado "falhou"~~ 3. ~~Compressão + deps~~ 4. ~~Cache de `Resolve`/`PathFor`~~

---

# Rodada 2

## 12. Bugs — perda/corrupção de dados

- [x] **Sweep da sub-drive compartilhada apaga o que foi criado localmente durante a listagem** — `SyncEngine.cs:1466-1519` + `RemoveLocal` :1299.
  A BFS por `children` roda fora de `_mutate`; um upload/mkdir que termina nesse intervalo ganha `ItemId` que não está em
  `seen` → `Sweep` → `RemoveLocal` apaga item e cache (filhos `Dirty` reaparecem na raiz do compartilhamento como cópias).
  Janela = duração da listagem, a cada 300 s. Correção: capturar `Db.AllRemoteIds(DriveId)` ANTES da listagem e varrer só
  `conhecidos − seen`.
- [x] **Mesma corrida na drive principal em enumeração completa** — `RunDeltaAsync` solta `_mutate` entre páginas (:1146) e
  `Sweep` roda no fim (:1159). Dispara na carga inicial, no 410/resync e em `RestoreDeletesAsync` (:955, zera o deltaLink):
  "Restaurar" exclusões com envios em andamento apaga localmente o que acabou de subir. HIPÓTESE quanto à Graph incluir
  na enumeração em curso o item recém-criado; a lógica local não tem guarda. Mesma correção.
- [x] **Desmontar/encerrar com fd de escrita aberto → alteração nunca sobe** — `AppHost.cs:199-205`, `VirtualFs.cs:653-661`,
  `SyncEngine.cs:64-70`. `fuse_session_exit` encerra sem `release`; `Write` já marcou `Dirty` (:527) mas o `Upload` só é
  enfileirado no `CommitWrite`; `Start()` não varre `Dirty` sem op (`AllDirty` foi removido na §4). Item fica "sincronizando"
  para sempre e o delta passa a ignorar versões remotas dele (:1249). Mesmo efeito em crash. Correção: no `Start()`, `Dirty &&
  !LocalOnly && sem Upload pendente → Enqueue`; no `UnmountAsync`, `CommitWrite` dos handles com `Wrote` antes do `Engine.Stop`.
- [x] **"Copiar link", "Versões" e "Baixar cópia" quebram em itens compartilhados/atalhos** — `ItemActions.cs:63,69,92,115-116,177,268`
  passam `drive.DriveId` (chave interna `<drive>|atalho|…`) em vez de `drive.ApiDriveId` (`SyncEngine.cs:675`) → Graph 400.
  `VirtualPathOf` (:230) também monta `/Host: Nome/...` inexistente → "Baixar cópia" dá ENOENT. Contradiz
  `docs/compartilhados.md:46` ("ApiDriveId em todas as chamadas").
- [x] **`mv` local para nome que já existe na nuvem substitui o arquivo da nuvem** — `GraphClient.cs:464` `conflictBehavior=replace`
  no Move; o VFS só confere o banco local. Arquivo criado por outro dispositivo após o último delta vai para a Lixeira sem
  aviso. Sugestão: `fail` + 409 tratado como no upload (`SyncEngine.cs:1794`). HIPÓTESE sobre suporte do SharePoint ao
  `conflictBehavior` em PATCH.
- [x] **Conflito de edição (412) com o arquivo aberto desvia as escritas seguintes para a cópia** — `HandleEditConflictAsync`
  :1887 faz `File.Move` do arquivo do cache; o VFS escreve pelo `SafeFileHandle` (:516) → escritas posteriores caem na cópia já
  enviada, e o original fica `Dirty` sem arquivo ("voltou à versão da nuvem", :1741-1757). HIPÓTESE (SQLite/logs/OBS no
  instante do 412). Correção: `File.Copy` quando `IsOpen(lid)`.
- [x] **`Create` chama `Open` segurando `Db.Lock`** — `VirtualFs.cs:403-409`. Se o delta inserir o item entre o lookup e o
  create: (a) força `Trunc` mesmo sem `O_TRUNC` → perda do conteúdo; (b) em item só-online o download roda dentro do lock →
  monitor reentrante preso → montagem inteira trava. HIPÓTESE (corrida). Correção: sair do lock antes do `Open` e só passar
  `Trunc` se vier no `flags`.
- [x] **Retomada de sessão "expirada" (404) após o último pedaço ter sido aceito** — `GraphClient.cs:70-74` reenvia → 404/416 →
  :388-392 "expirou" → recomeço com `conflictBehavior=fail` (:335) → 409 → cópia de conflito local + duplicata na nuvem.
  HIPÓTESE (depende da resposta real da Graph à URL de sessão concluída). Conferir com `GetChildByNameAsync` + hash antes de
  recomeçar.

**Feito (rodada 2, verificado em 2026-11-06):** todos os itens reproduzidos com teste RED antes da correção
(`Section12Tests`, `Rodada2MotorTests`, `Rodada2FilaTests`, `FilaA2Tests`, `FrenteA1Tests`). Correções que divergiram da sugestão
abaixo: 12.5 — o Move vai sempre com `fail`; sobrescrever um destino conhecido o exclui antes (com `If-Match`); 12.6 — os fds
abertos passam para a cópia de conflito (o `File.Copy` sugerido sobrescrevia a versão do outro dispositivo); 12.1/12.2 — os
"conhecidos" do início da enumeração ficam no banco (`delta_candidates`), o que também permite retomar após reinício.
**Na integração:** a fila paralela deixava Move/mkdir/envio de item novo passarem à frente de uma exclusão que aguardava o
período de silêncio — `mv` sobre destino virava conflito e `rm x` + criar `x` **perdia o arquivo novo na nuvem**; agora
esperam a exclusão do mesmo nome na mesma pasta (`IntegracaoFilaTests`). Dependem da Graph real (simulados no FakeGraph):
12.5 (`conflictBehavior` em PATCH no SharePoint), 12.8 (resposta da URL de sessão concluída).

## 13. Bugs — comportamento errado (motor)

- [x] **`_mutate` fica preso durante a transferência inteira do upload** — `OpsLoop` :1586-1591; `RunDeltaAsync` :1112,
  `RefreshSharedWithMeAsync` :1412 e `RestoreDeletesAsync` :944 esperam. Num envio de GBs a drive não recebe nada da nuvem,
  "Sincronizar agora" não faz nada, "Restaurar" exclusões trava a UI (e o shutdown, `AppHost.cs:108`). O lock só precisa cobrir
  o commit (:1842-1868), tratando "delta já inseriu este id" (hoje viraria cópia de conflito em :1203-1217). **É o bloqueio
  principal para uploads paralelos** (pendente da §3): depois dele, (a) `ClaimOps(drive, n)` marcando `ops.state='running'`
  sob `Db.Lock` (a coalescência de `Enqueue` :528-533 passa a excluir `running`); (b) dependências: `parent_lid` em `ops`, só
  reivindicar sem `mkdir` pendente/em execução no ancestral; serializar por `lid` (Upload→Move→SetTime); (c)
  `PendingDeleteFor` (:1696) assume ordem por id.
- [x] **Erro "transitório" persistente trava a fila para sempre** — `OpsLoop` :1598-1607 sem teto de tentativas;
  `Models.cs:113` classifica 423 (arquivo bloqueado/checked-out no SharePoint), 401 e 5xx como transitórios; backoff máx. 300 s.
  Um `.xlsx` aberto no Excel Online na cabeça da fila bloqueia todos os envios da drive indefinidamente, sem aparecer em
  Problemas. Correção: após N tentativas (ou 423 específico) → `FailPermanently`.
- [x] **"Descartar aviso" de um `mkdir` falho trava a fila** — `ParentRemoteId` :1658-1667: pai sem `ItemId`, sem op e sem
  `failed_ops` (após `DismissFailure`, `StateDb.cs:667`) → `parentPending` 503 "transitório" → caso acima para cada filho.
  Correção: sem `PendingOpsFor(parent, Mkdir)` → reenfileirar o mkdir do pai ou falhar permanente.
- [x] **Parciais órfãos nunca são apagados e contam no limite do cache → LRU despeja tudo** — `Cache.Delete` (`Support.cs:292`)
  e `DeleteDrive` (:297) não tocam `.partial/<drive>-<lid>-*` (`.partial`, `.stream`, `.blocks`); `DeleteOtherPartials` (:264)
  só limpa outras versões do mesmo lid. `ScanBytes` (:312-323) soma tudo; `EnforceCacheLimit` (`SyncEngine.cs:520-544`) só
  despeja linhas de `items`. Com órfãos ≥ 90 % do limite, a cada 2 min o LRU apaga TODO o cache não fixado (thrashing
  silencioso). Snapshots `.uploads` de ops removidas por `RemoveOpsForLid` também ficam até o próximo `CleanStale`.
- [x] **Limite de banda satura em ~8–16 MB/s qualquer que seja o valor** — `Graph/TransferBandwidth.cs:32,70`: fatia de 16 KiB
  + `Task.Delay(Clamp(…, 1 ms, …))`; acima de ~16 MB/s a espera devida (< 1 ms) vira ≥ 1 ms por fatia (granularidade do timer
  ~1–1,5 ms no Linux). Configurar 50 MB/s limita a ~10–16. HIPÓTESE no teto exato; medir. Correção: acumular dívida e dormir só
  quando ≥ ~5 ms, ou fatia maior em taxas altas.
- [x] Cópia de conflito desnecessária quando a nuvem só mudou metadados — 412 (:1812) vira cópia sempre; `HandleEditConflictAsync`
  já busca `remote` (:1875) mas não compara `remote.CTag == item.CTag` (rename/propriedade mudam eTag, não conteúdo).
- [x] Upload 409 de item novo nunca confere hash — :1794-1811: mesmo arquivo copiado de dois dispositivos vira "(conflito …)"
  em vez de adotar o id remoto (como `CreateFolderAsync` faz para pastas, `GraphClient.cs:440-446`).
- [x] `RetryFailure` perde `is_dir` — `StateDb.cs:692-694` (`failed_ops` não tem a coluna): exclusão de pasta que volta de Problemas
  aparece como arquivo em `HeldDeletes`/`trash`.
- [x] `ErrnoForDownload`/`HandleError` tratam ENOENT/EACCES do cache como "sem conexão" — `Errors.cs:11` (errno 2 ausente);
  `FileNotFoundException` cai em `IOException` → `Offline=true` (`SyncEngine.cs:984`). Cache apagado mostra "Sem conexão".
- [x] Retry de métodos não idempotentes — `GraphClient.cs:70-74` reenvia até 3× após `HttpRequestException/TaskCanceled`:
  `restoreVersion` (:588) pode restaurar duas vezes; PUT de arquivo vazio com `fail` (:298-305) pode dar 409 na 2ª.
- [x] `/permissions` sem paginação (:553) e `ParseCanWrite` (:563-571) só aceita `write`/`owner`: papéis `sp.*`/`fullcontrol` do
  SharePoint → compartilhamento tratado como somente leitura. HIPÓTESE (nunca testado em conta real).
- [x] `IsPolicyRefusal` (`ItemActions.cs:85`) inclui 400 genérico → qualquer 400 tenta `organization` e mostra o segundo erro.
- [x] `DeleteAsync` sem `If-Match` (:449-456): exclusão feita offline apaga versão editada por outro dispositivo nesse meio tempo
  (recuperável da Lixeira). Precisa de `ops.etag`.
- [x] Throttling 429/503 honrado por pedido (:85-93), mas 4 downloads + blocos + delta + ops continuam batendo na janela.
  Sugestão: "throttled até T" compartilhado por conta.

**Feito (rodada 2):** 13.1 em três etapas — o upload só pega `_mutate` no commit (que junta o id que o delta já tenha
inserido, sem violar `ix_items_remote`), fila com reserva (`ops.state`, `ClaimOps`) que não trava pela cabeça, e
**uploads paralelos** (`ParallelUploads`, padrão 3; metadados são barreiras curtas e não são ultrapassados para sempre).
13.5 medido: 50 e 100 MiB/s entregam 90–110 % do limite (antes ~13 MiB/s qualquer que fosse o valor). 13.14: bloqueio de
429/503 compartilhado por conta. 13.12: mantém a 2ª tentativa e mostra o 1º erro. 13.11: papéis `sp.*` são HIPÓTESE (a
issue onedrive-api-docs#1484 diz que a API devolve `owner`); aceitos de qualquer forma.

## 14. Bugs — FUSE/VFS

- [x] **Renomear/mover na nuvem com dentry em cache → o nome novo lista mas não abre (ENOENT)** — `InodeTable.cs:166-180` +
  `FuseHost.cs:254`. O delta troca `name`/`parent_lid` do lid L; o kernel ainda tem a dentry de `/a.txt` (ino L+1).
  `lookup("/b.txt")` → `GetOrAdd` acha `_byIno[L+1]` com `Path="/a.txt"` e não troca o Path (só adiciona alias) → todo request
  por inode (`open`, `opendir`, `getattr`, `xattr`) resolve `/a.txt` → ENOENT. Para pasta: `ls Docs2` dá ENOENT e os filhos
  ganham inodes virtuais. Dura até o kernel esquecer o inode (horas). Cenário: Dolphin aberto + rename no site/outro PC.
  Nenhum teste faz mudança remota com a montagem ativa. Correção: em `GetOrAdd` com `stable != null` e `existing.Path != path`,
  adotar o caminho novo como canônico; ou invalidação ativa (§18).
- [x] **Alias pendurado → `KeyNotFoundException` → EIO permanente no caminho** — `InodeTable.cs:96` vs :69/:107/:155. `Forget` só
  remove `_byPath[n.Path]`; o alias fica apontando para ino que saiu de `_byIno` → próximo `Lookup` lança → EIO até remontar.
  **Zero testes unitários de `InodeTable`.** Correção: guardar lista de aliases por nó e remover todos no `Forget`/`Detach`.
- [x] `getattr(ino)` de inode detached sem fh responde `EBADF` em vez de `ENOENT`/`ESTALE` — `FuseHost.cs:283-286` → `VirtualFs.cs:686`.
- [x] `readdirplus` com `path==null` grava `ino=0xffffffff` no `fuse_entry_param` (`FuseHost.cs:586`); "não ligado" é `ino=0`.
- [x] `open(O_RDONLY|O_TRUNC)` não trunca — `VirtualFs.cs:293-294` (`trunc = write && …`); com `ATOMIC_O_TRUNC` o kernel espera
  que o daemon trunque. HIPÓTESE quanto ao kernel.
- [x] `rmdir`/`mv` sobre pasta cujos únicos filhos são excluídos (sync seletiva) → `ENOTEMPTY` enquanto `ls` mostra vazia —
  `VirtualFs.cs:786`, :876 vs :194-195. Seguro, mas `rm -r` falha sem explicação.
- [x] mtime durante escrita: só o 1º `write` atualiza (`:524-528`), o `release` põe "agora" (`:645-651`); `SetAttr` trunca para ms
  (`FuseHost.cs:350`) → `make`/`cp -u` entre ext4 e a montagem podem ver "mais velho".
- [x] `link`/`symlink`/`mknod` → `ENOSYS` (não registrados em `BuildOps`); `EPERM` é o errno POSIX e `cp -a` trata melhor.
- [x] Raiz virtual: modo 0755 mas recusa create (`test -w` mente), mtime = agora a cada stat, entradas das drives no readdirplus com
  mtime 0 (1970) (`VirtualFs.cs:174,188`) → `ls -l ~/OneDrive` mostra 1970. Correção: 0555 + mtime fixo + `AttrOf(raiz da drive)`.
- [x] Nome com UTF-8 inválido: `S()` (`FuseHost.cs:208`) substitui por U+FFFD → arquivo criado com nome diferente; dois nomes
  inválidos distintos colidem. HIPÓTESE. `lookup` de nome > 255 bytes → `ENOENT` em vez de `ENAMETOOLONG`. `touch -d 1960` →
  `tv_nsec` negativo (`:173-177`).
- [x] Corridas (HIPÓTESE, janelas de ms): TOCTOU `Create`/`Mkdir`/`RenameLocked` com `dstParent` velho → item órfão cujo Upload cai
  em Problemas para sempre (`SyncEngine.cs:1660`); `readdirplus` concorrente do mesmo dir (`Peek`/`DropIfUnused`,
  `InodeTable.cs:48-52,86`) → referência do kernel que a tabela não conhece → `EBADF` até novo lookup; `Read` inline após
  `ReadNeedsNetwork=false` (`FuseHost.cs:452`) com blocos descartados entre a checagem e o `TryRead` → download na thread do loop.
- [x] SlowOps limite 256: > 256 reads presos na rede põem `fsync`/`rename`/`open` locais na fila atrás (inversão de prioridade).
  Trabalhos de sessão vazada rodam contra o `_fs`/`_inodes` da montagem seguinte (`FuseHost.cs:827-828`), inofensivo.

**Feito (rodada 2):** testes em `AnaliseFuseVfsTests`, `VfsRaceTests`, `VfsPerformanceTests`, `InodeTableTests` (FUSE real).
14.1/14.2: o lookup confere o lid do caminho com o inode guardado e move a subárvore; alias nunca fica pendurado; mais a
**invalidação ativa** do kernel (§18) a partir do delta (rename/exclusão/troca de conteúdo visíveis sem esperar timeout).
14.3: `fstat` em arquivo apagado mas aberto responde pelo handle (a sugestão ENOENT/ESTALE estava errada). 14.6: mantém
`ENOTEMPTY` (apagar levaria a subpasta excluída na nuvem) e explica na Atividade. 14.10: nome com UTF-8 inválido → `EILSEQ`.
14.14: operações locais têm fila própria (não esperam atrás de 256 reads presos na rede). Corridas da 14.13: despacho
conservador testado; a corrida do descarte de blocos não foi forçada. Estáticos de sessão FUSE vazada: não alterados (inofensivo).

## 15. Bugs — app, UI, integração

- [x] **"Entrar novamente" pela notificação com a janela na bandeja crasha o processo** — `MainViewModel.cs:610-614`:
  `Host.RequestShow()` só posta o `ShowWindow`; no mesmo callback `ReauthenticateAsync` → `MainWindow.axaml.cs:53-54`
  `ShowDialog(this)` com owner oculto → `InvalidOperationException("Cannot show window with non-visible owner.")` em
  `async void` → morte. O handler de `ConfirmRequested` (:81) já faz `if (!IsVisible) Show()`; o de login não. Com janela nunca
  criada (início minimizado) `AddAccountRequested` é null e o botão só abre a janela.
- [x] **Sem tratador global de exceções** (`UnhandledException`/`UnobservedTaskException` = 0 ocorrências): qualquer exceção em
  `AsyncRelayCommand`/`async void` (`AccountVm.Remove`→`SignOutAsync`; `DeleteAlertVm.Restore`; `App.Quit:291`→`Config.Save`
  com disco cheio; item acima) derruba o processo sem linha no `craftdriver.log`.
- [x] **2ª execução durante a espera do chaveiro** (pendência da §8): a 2ª instância cria outro `IpcServer` (`Ipc.cs:42` apaga o
  socket da 1ª) e tenta montar o mesmo ponto. Solução (~30 linhas): abrir o `IpcServer` em `Program.Main` antes do
  `StartWithClassicDesktopLifetime` com handler que delega a `App.Host?.HandleIpcAsync` quando existir e antes responde
  `ping`→ok, `show`→splash, demais→"ainda iniciando"; `AppHost.Start(IpcServer? existente)` reaproveita.
- [x] **SIGTERM não encerra limpo** — `Program.cs:230-240`: `ProcessExit` faz `stop.Set()` mas o runtime encerra ao retornar do
  handler; `ShutdownAsync` (Engine.Stop, IPC, `Config.Save`) não roda; a montagem só cai pelo `auto_unmount`. Afeta
  `systemctl --user stop` (unit `DesktopIntegration.cs:121-135` sem `ExecStop`) e logout; no modo gráfico idem (sem
  `SavePlacement`). Correção: `PosixSignalRegistration.Create(SIGTERM, …)` + `ExecStop=craftdriver quit`.
- [x] **Thread de UI bloqueada por processos externos** — `OpenPath`→`ShowFolder` (`DesktopIntegration.cs:296-317`, `WaitForExit`
  10 s; ativação D-Bus do Dolphin fechado leva segundos) chamado de `App.axaml.cs:161`, `MainViewModel.cs:592,109,66`;
  `InstallUserLauncherAndMenus:246` (5 s) e `SetAutostart`→`RunCommand:158` (3× 5 s) via `SaveSettings`. Enquanto isso o IPC
  `add-account` (`RunOnMain`) espera. Contradiz parcialmente a nota da §2 (só o `notify-send` foi resolvido).
- [x] **Trocar/remontar a pasta com montagem ocupada deixa config e UI mentindo** — `AppHost.UnmountAsync:199-205` (`fusermount -u`
  sem `-z`, `FuseHost.cs:922`); com EBUSY volta após 10 s com `Mounted` true; `MainViewModel.cs:775-776` grava o `MountPoint`
  novo e `Mount()` retorna calado (:184) → `MountText` mostra o novo "montado" enquanto o FUSE segue no antigo. HIPÓTESE no gatilho.
- [x] `ReadOnly` ignorado nos caminhos da instância — `AppConfig.Save:304` retorna calado; `RegisterAccount:240-248` responde
  "Conta conectada" ao IPC e `SaveSettings:778` diz "Configurações salvas." sem gravar.
- [x] Aviso do autostart engolido — `MainViewModel.cs:770-771` grava em `Message`, :784-785 sobrescreve com "Configurações salvas.".
- [x] `AppHost.Mount:190`: `ContinueWith` em task falhada registra "desconectado", `MountError` fica null → "Não montado" sem motivo.
- [x] `AppRestart.cs:16`: espera limitada a 30 s; se o antigo demorar mais (unmount 10 s + envio em curso) o novo acha o socket vivo,
  manda `show` (ignorado por `_quitting`) e sai → nenhuma instância fica. HIPÓTESE.
- [x] `DisplayScaling.Apply:70` grava `AVALONIA_SCREEN_SCALE_FACTORS` no ambiente do processo → herdado pelo relaunch do
  `AppRestart`, que então desliga o ajuste automático e congela a escala antiga.
- [x] `DesktopNotifier.cs:139`: `replaces_id` sempre 0 e nunca `CloseNotification` → "sessão expirada" fica na tela depois de entrar;
  `_callbacks` só limpa em `NotificationClosed`.
- [x] `ClipboardService.cs:57-64` só captura `Win32Exception`; `wl-copy` saindo antes do `WriteAsync` dá `IOException` (EPIPE) →
  "Broken pipe" em vez de "instale wl-clipboard". HIPÓTESE de timing. Unit systemd sem `WAYLAND_DISPLAY` importado → `share`
  pelo Dolphin falha porque quem copia é a instância (`:17-18`); fallback: o CLI copia (tem o ambiente da sessão).
- [x] `MainViewModel.FreeAll:706-721` só percorre `Engine.Drives` (não `SharedDrives`). HIPÓTESE.
- [x] CLI `pin|unpin|free|status` sem argumentos (`AppHost.cs:453-479`) responde `Ok` com texto vazio. `Ipc.cs:65` `ReadLineAsync`
  sem limite nem timeout por conexão; fallback `/tmp/craftdriver-<user>` (`AppConfig.cs:35`) 0755 sem conferir dono.
- [x] `DesktopIntegration.cs:168-172` aplica ` --minimized` a todas as linhas `Exec=` do autostart, inclusive `[Desktop Action]`
  (`DesktopFilesTests.cs:21` codifica o defeito como esperado). `Actions=` num autostart não tem função; falta `resume` no
  `.desktop` (`pause` duas vezes só repete "Pausado").
- [x] Nautilus `craftdriver.py:202-210`: `_poll_syncing` lê xattr no loop principal do GLib (bloqueia o Nautilus em FUSE lento);
  `update_file_info_full` enfileira todo arquivo de toda pasta (atalho pela tabela de montagens `fsname=craftdriver`); só API 4.0
  (Nautilus 42 do Ubuntu 22.04 falha no `require_version`); "Instalar atalho…" (`DesktopIntegration.cs:216-228`) ainda grava os
  Scripts → menu duplicado com a extensão.
- [x] Dolphin overlay relê todas as entradas em cache a cada 10 s enquanto o Dolphin viver (`craftdriveroverlayplugin.cpp:235-251`;
  até 4000 entradas ≈ 400 `getxattr`/s mesmo fora de vista) — limitar a `lastUsed` recente; plugin de ações lê xattr síncrono por
  item na thread de UI (`craftdriveractionplugin.cpp:340-351`); `craftdriver-dolphin/PKGBUILD.in:57` `arch=('x86_64')` só.

**Feito (rodada 2):** testes em `Secao15Tests` e afins (UiTests, processos reais para sinal e 2ª instância). 15.3: `flock` em
`run/craftdriver.lock` + IPC aberto antes do Avalonia ("ainda iniciando"). 15.4: SIGTERM/SIGINT/SIGHUP fazem o encerramento
completo (saída 0). 15.6: montagem ocupada mostra erro e mantém a pasta antiga (não força `-uz`). 15.16–15.18: autostart sem
`Actions=` e com `resume`; Nautilus sem xattr no laço do GLib, API 3.0, só arquivos da montagem, sem Scripts duplicados;
Dolphin sem xattr síncrono e sem reler tudo a cada 10 s, PKGBUILD com aarch64. Não verificado: sinal no modo gráfico real,
servidor de notificações real, Nautilus 42 e Dolphin reais.

## 16. Desempenho

- [x] `Counts` a cada 5 s = full scan de `items` por drive (`StateDb.cs:418-430`, `MainViewModel.cs:450`; `ix_items_remote` é
  parcial e não serve). Índice `items(drive_id)` ou cache por `ItemsVersion`.
- [x] `EnforceCacheLimit` N+1 — `SyncEngine.cs:526-529`: `CachedByLeastRecent` sem LIMIT (full scan + sort sem índice em
  `last_access`) e `IsEffectivelyPinned` (CTE) por item, a cada 2 min acima do limite.
- [x] `ApplyRemote` grava `Update` incondicional (:1271) → `ItemsVersion++` a cada lote de 32 → cache caminho→lid do FUSE invalidado
  continuamente durante a varredura de pasta compartilhada (50 k itens a cada 5 min, sem mudança). Comparar antes de gravar.
- [x] `RemoveLocal` de pasta grande = 1 transação sob `Db.Lock` com `RemoveOpsForLid` (`StateDb.cs:585`, sem índice `ops(lid)`) e
  `HasPendingOp` (:704) por item → fila de 10 k × delta grande = milhões de linhas sob o lock; FUSE congela.
- [x] Alocações LOH: `GraphClient.cs:347` `buf` 10 MiB + :355 `.ToArray()` (cópia desnecessária) = 20 MiB/pedaço; `:220/:258`
  256 KiB por chamada; `Streaming.cs:336` 4 MiB por bloco. `ArrayPool`.
- [x] Token MSAL por requisição — `GraphClient.cs:55` chama `_token` em todo `SendAsync` → `AcquireTokenSilent` → `MsalCacheHelper`
  relê o chaveiro (D-Bus/libsecret). Um bloco de streaming = uma ida ao chaveiro. HIPÓTESE no custo; cachear até perto de expirar.
- [~] `InodeTable.Subtree` é varredura linear de `_byPath` (`InodeTable.cs:159-164`) por `unlink`/`rmdir`/`rename`: após um `find`
  com 100 k caminhos, `rm -rf` de 10 k = 10 k × 100 k `StartsWith` sob o lock. Índice de filhos por diretório.
- [x] Lookup negativo não é cacheado — `FuseHost.cs:273-276` responde `ENOENT` em vez de `fuse_reply_entry` com `ino=0` +
  `entry_timeout` → `git status`, shells sondando PATH, Dolphin procurando `.directory` repetem o round-trip.
- [x] `readdirplus` refaz um SELECT por entrada para achar o lid que `ReadDir` já tinha (`FuseHost.cs:582` → `Peek` → `Resolve`;
  `VirtualFs.cs:194-195` tem `c.Lid`). Cada lookup = 2 `Resolve`; cada `getattr` de arquivo em cache = 3 `stat()` de disco
  (`AttrOf` :155-161).
- [x] xattr do Dolphin: `StatusOf` (`VirtualFs.cs:1070-1077`) = CTE + stat por arquivo + `Resolve` em cada `getxattr`/`listxattr`;
  KIO ainda pede `user.xdg.*`. Conjunto de fixados em memória (como `_excluded`).
- [x] `GetChildNoCase` para nome não-ASCII (`StateDb.cs:294-300`) carrega todos os irmãos não-ASCII e normaliza cada um: O(n) por
  `create`/`mkdir`/`rename` numa pasta com 10 k "Relatório…".
- [x] `Trash()`/`RemoveTrash` usam a conexão principal (`StateDb.cs:788-810`) → janela Lixeira espera `Db.Lock`.

**Feito (rodada 2), com medição:** `ls -l` repetido de 10 k: 30.012 → 10.008 SELECTs (cache de caminhos LRU de 65.536);
1.000 lookups negativos: 3.000 → 3 SELECTs (cache negativo do kernel); `GetChildNoCase` não-ASCII: 200 creates 10,6 s → 0,67 s;
token MSAL reaproveitado até perto de expirar (antes 2 idas ao chaveiro por requisição); buffers do `ArrayPool`;
`ApplyRemote` só grava o que mudou; Lixeira pela conexão de leitura. **Parcial:** índice de filhos na `InodeTable` — 10 k
remoções numa tabela de 100 k: 16,7 s → 31 ms isolado, mas o `rm -rf` real não mudou (~30 s): o gargalo é outro.
`Counts`/`EnforceCacheLimit`: só os índices da §17 (a medição mostrou que não valiam mais que isso).

## 17. Esquema, estado, remoções

- [x] Migrações por `try/catch SqliteException` (`StateDb.cs:141-148,159-163`) engolem qualquer erro (SQLITE_FULL, I/O, só-leitura)
  como "já existe" → próximo SELECT quebra com "no such column". Usar `PRAGMA table_info`/`PRAGMA user_version`.
- [x] Órfãos: `DeleteDriveRows` (:447-456) não apaga `conflicts`, `trash`, `excluded` da drive (nem das sub-drives) → aba "Conflitos (N)"
  conta conflitos de conta removida; Lixeira lista itens de drive removida. `_excluded` em memória não é recarregado após
  `DeleteDriveData`.
- [x] Índices faltando: `ops(lid)`, `items(drive_id)`, `items(pinned) WHERE pinned=1`, `items(last_access) WHERE cached_ctag IS NOT
  NULL`, `failed_ops(drive_id)`, `conflicts(drive_id)`, `excluded(drive_id)`.
- [x] `FailPermanently` dedupe exige `lid != 0` (:597): exclusões com lid 0 (`SyncEngine.cs:1682,1848`) acumulam falhas repetidas.
- [x] **Estático mutável**: `SyncEngine.MaxDownloadResumes/DownloadAttempts`, `DriveSync.SharedPollSeconds/SharedMaxItems/
  DeleteQuietMs/UploadSettleMs/MassDeleteWindow/DeltaBatchSize/PinBatchSize` — mesma classe do `ChunkSize` que a §10 diagnosticou
  como causa de testes instáveis; 6 arquivos de teste os alteram em paralelo. Mover para `AppConfig`/instância.
- [x] Mortos: `Item.Clone` (`StateDb.cs:34`), `StateDb.ConflictCount` (:665), `ContentCache.TotalBytesAsync` (`Support.cs:308`;
  a nota da §3 cita este método, mas o App usa `TotalBytes` via `CacheUsageSampler`), `GraphDrive.OwnerName` (`Models.cs:73`),
  `AppHost.RequestVersions/RequestTrash` (:506-507), `StartupCoordinator.Skipped` (:33), `AppHost.CreateAsync()` sem parâmetros
  (só testes), `LoginErrors.cs:50/:56` testam `AADSTS90094/90095` duas vezes, `RemoveLocal` :1332-1335 `if (IsOpen…) { }` vazio,
  `GraphClient.cs:177` e :534 duplicam a lista de campos, `craftdriver-symbolic.svg` em 5 lugares sem consumidor.
- [x] `Program.RunCli:124-127`: três cadeias de `if` por comando para montar `rest` → tabela comando→(resolve caminhos?, flags?).
  Estilos `h1/h2/muted/card` em `App.axaml` e o resto em `CraftDriverStyles.axaml`: uma fonte só.

**Feito (rodada 2):** migrações por `PRAGMA table_info` (erro real sobe), órfãos de drive removida, os 7 índices
(conferidos com `EXPLAIN QUERY PLAN`), estáticos mutáveis por instância/`AppConfig`, código morto removido (exceto o
`craftdriver-symbolic.svg`, que é instalado no tema e pode ser usado pelo shell), `RunCli` por tabela, estilos numa fonte só.

## 18. Features e refinamentos

- [x] **Invalidação ativa a partir do delta**: `fuse_lowlevel_notify_inval_entry`/`notify_delete`/`notify_inval_inode` quando
  `ApplyRemote` renomeia/move/apaga/troca cTag — corrige §14.1 na origem, permite timeouts maiores e gera eventos inotify para o
  gerenciador de arquivos (HIPÓTESE). Não chamar de dentro de handler nem com `Db.Lock`.
- [x] Adotar em vez de conflitar quando o hash bate (409 novo item; 412 com `remote.CTag == item.CTag`).
- [x] Restaurar exclusões sem re-enumerar a drive: `RestoreDeletesAsync` faz `GetItemAsync` de cada `remote_id` e reinsere.
- [x] Persistir `nextLink` da enumeração inicial (`drives.delta_link` só no fim, :1162) para retomar após reinício em drives enormes.
- [~] Delta por pasta no OneDrive pessoal para sub-drives (doc já lista): `Kind=="personal"` → `/drives/{dono}/items/{id}/delta`,
  fallback para children em 400/404. Substituto da `sharedWithMe` obsoleta: HIPÓTESE Microsoft Search `POST /search/query`
  com `sharePointOneDriveOptions.includeContent:"sharedContent"`.
- [x] Painel de fila: `ops` já tem `attempts/last_error/next_try/created` — expor "o que está travando a cabeça da fila".
- [x] Cache negativo no lookup; `ReadDir` devolve lid → `Peek(path, lid)` sem SELECT; `link`/`symlink`/`mknod` → `EPERM`.
- [~] UI: `Show()` antes de `ShowDialog` em todos os handlers da `MainWindow`; `Ctrl+Q` confirmar com transferências ativas;
  listas de fixados/contas (`MainWindow.axaml:153-173,233-245`) sem `ScrollViewer`; Problemas/Conflitos sem virtualização
  (`ListBox`); caminhos com `TextTrimming` sem `ToolTip` (:112,239,264,297); Atividade sem busca/filtro/copiar (teto 200);
  `NoTrayCloseWindow.axaml:12` com `IsDefault` em "Encerrar" (oposto à regra da `ConfirmWindow`); tema só aplica ao Salvar;
  glifos ☁✔⟳📁🗑 em texto dependem de fonte emoji (só "recommends") → ícones vetoriais; "Adicionar biblioteca" sem `CanExecute`
  (ativo sem conta corporativa); i18n por recursos (tudo pt-BR inline).
- [~] Plugin do Dolphin em `.deb/.rpm` para distros KF6 (Fedora, Ubuntu 24.10+, Debian 13) compilado em contêiner no CI;
  `aarch64` no PKGBUILD dele; publicação automática no AUR (segredo SSH); metainfo `<releases>` gerada do CHANGELOG.

**Feito (rodada 2), exceto:** i18n por recursos (fora de escopo; tudo continua em pt-BR) e a publicação no AUR, que tem
o job pronto mas só roda com o segredo `AUR_SSH_KEY` (sem ele, pula com aviso). Delta por pasta no OneDrive pessoal: feito
com fallback para `children`, **Graph real não verificada**. Plugin do Dolphin em .deb/.rpm (Debian 13, Ubuntu 25.04,
Fedora 41) compilado e testado (CTest) em contêiner; ARM não. Painel "Envio parado" mostra o que trava a cabeça da fila.

## 19. Empacotamento, CI e documentação

- [x] **`install.sh:30-31` ainda publica single-file** (`PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`) — contradiz a
  nota da §9 e o CHANGELOG; o modo usuário continua extraindo em `~/.net/CraftDriver.App/<hash>/` (e `RemoveLegacyLeftovers`
  só limpa `OneDriveLinux.App`). `--uninstall` (l.11-20) não remove SVGs em `scalable/symbolic`, a unit systemd nem `~/.net`.
- [x] **README "Limitações conhecidas" desatualizado** (l.172-176): ainda diz "sem streaming por faixas" e "atalhos e compartilhados
  ainda não aparecem" — os dois existem. `PENDENCIAS §1` nota do `nullpath_ok` obsoleta (na API low-level o mecanismo é
  `Detach` + fh); `docs/desempenho-fuse.md` não lista `copy_file_range` entre os SlowOps.
- [x] Metainfo `<releases>` só tem 0.1.4 (`metainfo.xml:76-78`), sem `<description>`/`<screenshots>`, e `build.sh` não a
  templatiza → a tag `v0.1.5` sai dizendo que a última é 0.1.4. `csproj` `<Version>0.1.4</Version>` não é bumpado (o CI injeta
  a tag). `checks` não confere tag ↔ `<Version>` ↔ `<release>` ↔ data do CHANGELOG, nem roda `actionlint`/`appstreamcli
  validate`/`desktop-file-validate`; "0 avisos" não é imposto (`-warnaserror`); cobertura sem limiar; `release:212-215` baixa todos
  os artefatos com `merge-multiple` (HTML/PNGs para `art/`); `dolphin-plugin` faz `pacman -Syu` sem cache.
- [x] Pré-requisitos do runtime .NET ausentes dos pacotes: `libssl3|libssl1.1`, `libgssapi-krb5-2`, `libstdc++6`, `zlib1g` (deb);
  `openssl-libs`, `krb5-libs` (rpm); `openssl`, `krb5` (Arch) — o smoke nunca faz TLS. HIPÓTESE: base sem `libssl` → login falha
  com `DllNotFoundException`. `PKGBUILD.in:10` `libgcc`/`libstdc++` (nomes do split do gcc 16) quebram em Manjaro
  estável; o PKGBUILD do dolphin usa `gcc-libs`.
- [x] `CraftDriver.Fuse.csproj` fora da análise de trimming (`IsTrimmable`/`EnableTrimAnalyzer` ausentes; só o smoke cobre).
- [x] Unit systemd (`DesktopIntegration.cs:121-135`): sem `ExecStop`, sem `After=`, ambiente sem `WAYLAND_DISPLAY`.
- [x] Tarball sem script de (des)instalação em modo usuário (`install.sh` exige SDK).
- [x] **46 arquivos modificados + 110 novos sem commit desde `fb03c53`** (6,5 k linhas): risco de perda; commitar por tema quando
  decidir (o repositório é público — conferir que nada em `tests/`/`docs/` cita conta ou caminho pessoal antes).

**Feito (rodada 2):** 19.4 conferido com os pacotes instalados em debian:12-slim, ubuntu:24.04 e fedora:41 (login chega
ao código de dispositivo; antes: core dump por falta de libssl no Debian e `UntrustedRoot` no Ubuntu); o smoke agora testa
TLS. `install.sh` sem single-file e tarball com instalador em modo usuário (sem SDK). O CI confere tag ↔ `<Version>` ↔
metainfo ↔ CHANGELOG, roda actionlint/appstreamcli/desktop-file-validate, compila com `-warnaserror` e usa
`--blame-hang-timeout` (o travamento do UiTests na release v0.2.0 passa a dizer qual teste). Cobertura sem limiar (só
informativa). Não verificado: o CI novo só roda no próximo push.

## 20. Lacunas de testes (rodada 2)

- Mudança remota (rename/move/delete/recriar) com a montagem ativa e dentries em cache; `InodeTable` unitário (alias → Forget
  → Lookup; `Peek`/`DropIfUnused` concorrente; `Move` com aliases).
- Desmontar/encerrar/crash com fd de escrita aberto → reiniciar → conteúdo sobe.
- Sweep × upload concluído durante listagem (sub-drive e enumeração completa); `RestoreDeletesAsync` com envio em curso.
- Ações (`share`/`versions`/`trash`) em item de sub-drive compartilhada.
- `open(O_RDONLY|O_TRUNC)`; `open(O_CREAT)` sem `O_TRUNC` sobre item que apareceu via delta; `rmdir` de pasta só com filhos
  excluídos; `ln`/`ln -s`/`mkfifo`/`flock`/`touch -a`/`UTIME_OMIT`/data pré-1970/UTF-8 inválido; `ls -l ~/OneDrive` (1970);
  `readdir` com `rm` concorrente; Ctrl-C em `rename`/`truncate`; saturação de SlowOps; unmount com SlowOps > 15 s + remontar.
- Fila: 423 persistente na cabeça; `DismissFailure` de mkdir com filhos; teto do limitador de banda (medir MB/s reais).
- UI: "Entrar novamente" pela notificação com janela oculta; SIGTERM em `--headless`; `ReadOnly` + Salvar; remount com EBUSY.
- Escala: `rm -rf` após `find` grande; `ls -l` de 10 k entradas (contagem de SELECTs); padrão Dolphin por arquivo.

## 21. Não verificado (rodada 2)

- Tudo por leitura estática (subagentes sem build/test); nenhum item da rodada 2 foi reproduzido, salvo onde a mecânica é direta
  (`ApiDriveId`, `install.sh`, README, ausência de `PosixSignalRegistration`/`UnhandledException`, `conflictBehavior=replace`).
- Graph real: inclusão de itens criados durante a enumeração (§12.2), resposta da URL de sessão concluída (§12.8),
  `conflictBehavior` em PATCH no SharePoint, papéis `sp.*`, substituto da `sharedWithMe`, custo do `MsalCacheHelper` por pedido.
- Kernel: `O_RDONLY|O_TRUNC` com `ATOMIC_O_TRUNC`; `d_splice_alias` em rename remoto; `notify_delete` gerar inotify.
- Áreas que pareceram sólidas: promoção do streaming, retomada de download por item+cTag, coalescência upload×edição,
  exclusões recursivas, `AdjustableLimiter`, reconciliação de sub-drives, cache caminho→lid (`ItemsVersion`), `Reserve`/
  `Unreserve`, respostas FUSE (cada caminho responde uma vez), `Unmount`, notificações D-Bus, `TrayHostProbe`,
  `WindowPlacement`, CI (permissões mínimas, gating, artefatos).

## Ordem sugerida (rodada 2)

1. §12 inteira (perda de dados; os 4 primeiros têm correção pequena e mecanismo confirmado).
2. §15.1–15.4 (crash do "Entrar novamente", tratador global, IPC cedo, SIGTERM) + §19.1 (`install.sh`).
3. §13.1–13.4 (`_mutate` no upload → destrava uploads paralelos; teto de tentativas; parciais órfãos).
4. §14.1–14.2 (InodeTable) + §16 índices e cache negativo.
5. §19 documentação (README, metainfo, checks de release).
