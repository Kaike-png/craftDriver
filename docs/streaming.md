# Leitura por faixas (streaming)

Arquivos grandes só-na-nuvem abrem na hora para **leitura** e baixam só os blocos lidos, com `Range: bytes=a-b`.
Antes, o `open()` baixava o arquivo inteiro (um vídeo de 4 GB só abria depois de 4 GB; ler o cabeçalho custava
o download todo).

## Quando vale

`SyncEngine.CanStream`: arquivo remoto sem alterações locais, tamanho ≥ `AppConfig.StreamingThresholdMB`
(padrão 8 MB; 0 desativa) e que não seja documento do Office numa drive do SharePoint/OneDrive for Business (lá o
servidor injeta propriedades e o arquivo baixado difere do tamanho/hash informados — as faixas não bateriam).
Abrir para escrita (`O_WRONLY`/`O_RDWR`/`O_TRUNC`), `truncate`, renomear para nome só local e fixar continuam
exigindo o conteúdo inteiro (`EnsureContentAsync`), como antes.

## Desenho

- **Sessão por versão** (`StreamSession`, `src/CraftDriver.Core/Sync/Streaming.cs`): uma por item+cTag com
  handles abertos (`SyncEngine._streams`). Arquivo de trabalho esparso `{parcial}.stream` (tamanho final via
  `SetLength`, só os blocos baixados ocupam disco) e bitmap `{parcial}.blocks` (cabeçalho `CDS1` + tamanho do
  bloco + tamanho do arquivo, depois 1 byte por bloco), com `{parcial}` = `ContentCache.PartialPathFor` (mesmo
  nome por item+versão dos downloads retomáveis).
- **Bloco** = `AppConfig.StreamingBlockKB` (padrão 4 MiB). Cada bloco é um pedido
  `IGraphApi.DownloadRangeAsync` (novo; `GraphClient` exige 206 e o número exato de bytes — 200 "ignorei o
  Range" é erro), com a mesma vaga (`DownloadConcurrency`) e limite de banda dos downloads da drive.
- **Ordem de gravação**: o bloco chega inteiro na memória → grava no esparso → `fsync` → marca o byte do bitmap.
  Bloco marcado sempre tem dados; bloco que falhou não é marcado e o read devolve erro
  (`Errors.ErrnoForDownload`: `EHOSTUNREACH` sem rede, `EIO` etc.), nunca zeros.
- **Reinício**: o bitmap é relido se cabeçalho, tamanho do bloco, tamanho do arquivo e tamanho do esparso
  conferem; senão os dois arquivos são recriados. Fechar o último handle para o segundo plano e mantém os blocos.
- **Leitores do mesmo bloco** esperam um único pedido; o pedido é cancelado quando ninguém mais o espera (Ctrl-C
  do único leitor, último `close`, segundo plano parado).
- **Leitura sequencial** (≥ min(bloco, 1 MiB) seguidos no mesmo handle) dispara o download do resto em segundo
  plano, a partir da posição atual e depois do começo, um bloco por vez.
- **Completo**: quando o último bloco chega, o arquivo inteiro é verificado com `VerifyDownload` (tamanho +
  QuickXorHash, igual ao download normal) e só então `File.Move` para o caminho do cache + `CachedCTag` (com
  `Db.Lock`). Os fds abertos seguem o mesmo inode. Não confere → aviso em Atividade e **todos** os blocos são
  descartados (não dá para saber qual veio errado); leituras seguintes baixam de novo.
- **Nuvem mudou** (cTag nova): o próximo `open` cria sessão nova e apaga os arquivos de outras versões
  (`DeleteOtherPartials`, agora preservando só `{parcial}*` da versão atual). A sessão velha fica "desligada":
  seus fds leem o que já baixaram; bloco que falta dá `EIO` (a versão não existe mais na nuvem) e ela nunca vira
  cache.
- **Disco do cache cheio**: o bloco que não coube não é marcado, o read recebe `ENOSPC` e o pedaço já gravado
  é devolvido com `fallocate(PUNCH_HOLE)` (o LRU só conta blocos marcados; sem isso o espaço ficaria preso no
  esparso). No download inteiro (`open`), o parcial é apagado no disco cheio em vez de ficar para retomar.
- **Transferência na janela**: enquanto há blocos baixando existe uma `Transfer` com total = tamanho do arquivo e
  progresso = bytes já no disco + o bloco em curso. "Cancelar" interrompe os pedidos; o segundo plano não recomeça
  sozinho naquela sessão; um read que estava esperando recebe `ECANCELED`.
- **Cache/LRU**: o esparso fica fora do caminho do cache até completar (o LRU nunca o vê); os handles reservam o
  item (`Reserve`) como antes, então depois de promovido também não é despejado enquanto aberto. O total do cache
  conta só os blocos baixados (`StreamSession.StoredBytes`), não o tamanho aparente do esparso.

## FUSE

- `open` (já em `SlowOps`) responde sem rede; o registro do último open do inode é invalidado, para o próximo
  open não receber `keep_cache` — e open sem `keep_cache` faz o kernel descartar as páginas do inode, então
  páginas de blocos ainda não verificados nunca passam para outro open.
- **`direct_io` × cache de página** (decidido no `init` da sessão, `FuseHost.StreamingDirectIo`):
  - libfuse ≥ 3.17.3 e kernel ≥ 6.6 (Arch, Fedora): negocia `FUSE_CAP_DIRECT_IO_ALLOW_MMAP` e responde o open por
    faixas com `direct_io`. O read devolve o errno real (`ENOSPC`, `EHOSTUNREACH`, `EINTR`), um open nunca espera
    a página que outro processo está baixando, e o `mmap` funciona.
  - libfuse mais antiga (Ubuntu 24.04 e Debian 12 têm 3.14): sem `direct_io`, pelo cache de página. Com `direct_io`
    o `mmap` daria `ENODEV` (git com packs grandes, SQLite, visualizadores), o que é pior que os custos deste modo:
    erros de leitura chegam ao programa como `EIO` (o kernel converte), e um open sem `keep_cache` espera enquanto
    outro processo tem uma página do mesmo arquivo travada aguardando a rede.
  - Provas: `FileOpsTests.Mmap_de_arquivo_grande_em_leitura_por_faixas` nos dois modos (`ForcePageCacheStreaming`);
    sem a negociação, o `direct_io` volta a dar `ENODEV`. Os testes de ENOSPC e de dois leitores simultâneos rodam
    no modo `direct_io` desta máquina; o modo de cache de página não tem essas garantias.
- `read` decide na thread do loop (`VirtualFs.ReadNeedsNetwork`): faixa já no disco responde inline; bloco que
  falta vai para `SlowOps` e espera a rede lá, cancelável com `fuse_req_interrupted` (`EINTR`).

## Testes

`tests/CraftDriver.Tests/StreamingTests.cs` (FakeGraph conta faixas e bytes entregues): 64 MB, ler 4 KB do
meio baixa 1 bloco e o open não baixa nada; arquivo pequeno continua inteiro; leitura sequencial promove com hash
e cada bloco vem uma vez; bloco corrompido → hash final reprovado, nada vira cache, blocos descartados e
rebaixados; rede caída → `EHOSTUNREACH` e o bloco não é marcado; open de escrita completa e reaproveita os blocos;
cTag nova → open seguinte lê a versão nova e o fd velho dá `EIO` no que falta; bitmap sobrevive a reinício do
motor; LRU; Ctrl-C cancela o pedido do bloco; segundo plano com progresso e cancelamento pela janela.
`StreamingFuseTests`: montagem real com **1** thread no loop e a rede presa — `python` faz `open` (volta logo),
`pread` no meio fica esperando e um `stat` responde em < 1 s; depois confere os bytes (o segundo `pread` igual pode
vir do cache de página), `sha256sum` do arquivo inteiro e promoção ao cache. `GraphProtocolTests.Download_de_faixa_…`:
`Range` com fim, faixa truncada e servidor que ignora `Range`.

## Não coberto

- Graph real (só o servidor HTTP falso dos testes): conferir que o redirecionamento para a URL pré-autenticada
  mantém `Range` com fim e responde 206 (o download retomável já dependia disso para `Range` aberto).
- Arquivos ≥ 512 MB: como no download normal, a verificação final é só de tamanho (sem QuickXorHash).
- `mmap` de arquivo em streaming: coberto só para leitura. O modo de cache de página (libfuse < 3.17.3) só é
  exercitado por esse teste; num Ubuntu/Debian real não foi testado.
