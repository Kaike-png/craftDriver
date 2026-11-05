# Desempenho: saturação do FUSE por downloads síncronos

## Reprodução local (sem conta ou rede real)

```sh
~/.dotnet/dotnet run --project tools/FuseSaturationProbe/probe.csproj
```

Requer `/dev/fuse`, libfuse3, `fusermount3` e Python 3. Usa somente `FakeGraph`, cache e banco descartáveis; não usa o perfil do usuário. Não execute simultaneamente aos testes que montam FUSE no mesmo processo.

O programa monta o VFS com 16 workers, inicia 16 leituras de arquivos somente-online e bloqueia artificialmente a conclusão dos downloads. Depois tenta um `stat` de um caminho inexistente; essa operação não precisa baixar nada. Os downloads são liberados quatro segundos depois. O mount é desmontado no `finally`.

Resultado observado antes das otimizações da seção 3:

```text
downloads_entered=16; child_still_blocked=True
stat_seconds=2.125146821999806
python_exit=0 stderr=
```

O `stat` começou aproximadamente dois segundos depois das leituras e só terminou quando os downloads foram liberados: reprodução de saturação dos workers. `child_still_blocked` sozinho não prova saturação (o executor Python também espera as leituras); a medida relevante é `stat_seconds`.

Depois da migração para a API de baixo nível (mesmo programa; o motor baixa 4 por vez, então só 4 downloads
chegam a entrar, mas os 16 `open()` estão pendentes):

```text
downloads_entered=4; child_still_blocked=True
stat_seconds=0.001425331000064034
python_exit=0 stderr=
```

## Por que não bastava Task.Run

A documentação oficial da libfuse diferencia a API de alto nível, síncrona, da API de baixo nível, assíncrona:
https://libfuse.github.io/doxygen/ . Na de alto nível a resposta ao kernel só existe quando o callback retorna;
`Task.Run(...).GetAwaiter().GetResult()` mantém o worker preso do mesmo jeito, e aumentar threads só desloca o limite.

## Como ficou (API de baixo nível)

- `FuseHost` usa `fuse_session_new`/`fuse_session_loop_mt` e responde com `fuse_reply_*`, que pode ser chamado de
  qualquer thread. `open`, `setattr` com tamanho (truncate), `rename` e `fsync` — os únicos que podem baixar ou ir ao
  servidor — rodam em `SlowOps` (e o `read` de um bloco ainda não baixado da leitura por faixas, ver `streaming.md`) (threads próprias, sob demanda, até 256) e respondem quando terminam; a thread do
  loop volta na hora. O resto é local e responde na própria thread.
- O `fuse_file_info` da libfuse fica na pilha do loop: o `open` assíncrono responde com uma cópia.
- `SlowOps` não usa o ThreadPool: os handlers esperam bloqueados o download assíncrono, que precisa do ThreadPool.
- `InodeTable` traduz inodes ↔ caminhos para o `VirtualFs` (que continua por caminho): contagem de lookups por
  resposta de entrada (lookup, mkdir, create, readdirplus — só entradas que couberam no buffer) e `forget`; `rename`
  move a subárvore; `unlink`/`rmdir`/rename por cima soltam o inode do caminho, e os fds abertos seguem pelo `fh`
  (o que `hard_remove` + `nullpath_ok` faziam). Números não são reaproveitados.
- Reproduzido da API de alto nível: prazos de cache de 1 s (entry/attr), `auto_cache` (mantém as páginas entre
  opens só se mtime e tamanho não mudaram) e `default_permissions`/`auto_unmount`.
- Ctrl-C: `fuse_req_interrupted(req)` por requisição (a API de baixo nível não descarta o `FUSE_INTERRUPT`, nem
  precisa de SIGUSR1 nas threads). O download pendente é cancelado e o `open` devolve `EINTR`.
- Desmontar com respostas pendentes: a sessão só é destruída quando todas responderem (até 15 s; depois disso é
  vazada de propósito, para não escrever num fd fechado ou reaproveitado).
- Requer libfuse ≥ 3.12 (já exigido antes, pelo `fuse_loop_cfg_*`). A tabela de operações é informada até `lseek`.

## Testes

- `NonBlockingFuseTests`: 4 threads do loop, 6 `cat` presos em downloads; `stat` de inexistente + `ls` precisam
  responder em < 1 s, e os 6 `cat` recebem o conteúdo certo quando os downloads são liberados. Falhava antes.
- `InterruptTests`: Ctrl-C num `cat` cancela o download.
- `FuseIntegrationTests.Inodes_diretorio_grande_e_renomeacoes`: 700 nomes de 200 caracteres (vários readdir),
  inodes estáveis, pasta renomeada com filhos já vistos, mv por cima e reescrita/truncate do mesmo nome.
