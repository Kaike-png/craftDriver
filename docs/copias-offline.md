# Cópias offline dos itens fixados (app encerrado)

No Windows, o OneDrive guarda os arquivos no próprio NTFS. Um filtro do kernel (Cloud Files API) continua ativo
mesmo com o app fechado, então os itens "Sempre manter neste dispositivo" seguem na pasta. No Linux, `~/OneDrive` é
uma montagem FUSE servida pelo processo do CraftDriver. Ao **Encerrar**, a montagem sai e aparece a pasta real que
estava por baixo. Antes ela ficava vazia.

## Ao encerrar (`AppHost.ShutdownAsync` → `OfflineCopies.Materialize`)

Só roda quando a instância montou de verdade e a desmontagem funcionou: se a pasta estiver em uso (EBUSY) ou montada
por outro processo, nada é gravado.

- Para cada item fixado (fora de pastas excluídas), grava o conteúdo do cache no mesmo caminho dentro da pasta real
  de `MountPoint`. Usa **reflink** (`ioctl FICLONE`): no btrfs/XFS as duas cópias dividem os blocos, sem gastar
  espaço. Em outros sistemas de arquivos faz uma cópia comum.
- Mantém a data (mtime, com a fração em ns) e as permissões. Compartilhado só leitura vira 0444.
- Ficam de fora:
  - arquivos ainda não baixados;
  - itens só locais (temporários);
  - qualquer caminho que já exista na pasta real (nunca sobrescreve);
  - itens com exclusão aguardando confirmação (e o que estiver dentro deles): o usuário os apagou com o app fechado.
- O manifesto vai para a tabela `offline_copies` (caminho, lid, tamanho, mtime em ticks), **aos poucos**: cada
  entrada é gravada ANTES de o item aparecer na pasta real. Pastas: registra e cria. Arquivos: copia com nome
  temporário (`.craftdriver-parcial-<lid>`), registra e só então renomeia para o nome final (sem sobrescrever).
  `offline_copies_state.complete` só vira 1 no fim. Assim, se o processo morrer no meio (desligamento que não
  esperou), nada fica na pasta com o nome final sem estar no manifesto, e nenhum arquivo pela metade tem nome final.

## Desligar o PC com o app aberto (`ShutdownInhibitor`)

Ao iniciar, o app pega uma trava **delay** de desligamento no logind (`Inhibit("shutdown", …, "delay")`, visível em
`systemd-inhibit --list`). No sinal `PrepareForShutdown(true)` ele faz o mesmo encerramento do SIGTERM/"Sair"
(desmontar, gravar as cópias, parar o motor); o processo saindo fecha o descritor e o desligamento segue. O logind
espera no máximo `InhibitDelayMaxSec` (5 s por padrão): é o bastante com reflink (btrfs/XFS); em outros sistemas de
arquivos e muitos fixados grandes a cópia pode não terminar, e aí vale o caso "interrompido" abaixo. Sem logind
(contêiner, outro init) só fica uma linha no log. `CRAFTDRIVER_NO_LOGIND=1` desliga (os UiTests usam).

## Ao abrir (`AppHost.Start` → `OfflineCopies.Import`, ANTES de `SyncEngine.Start`)

A ordem é obrigatória, e o `Import` recusa se o motor já tiver drives. Com o delta rodando antes, um arquivo editado
nos dois lados adotaria a versão da nuvem, e o envio seguinte a sobrescreveria. Como a importação vem antes, o item
fica `Dirty` com o eTag antigo, e o envio recebe 412, o que gera a **cópia de conflito** de sempre.

**Encerramento interrompido** (`complete` = 0): o que está no manifesto e falta na pasta nunca chegou a ser copiado,
então não vira pendência de exclusão nem origem de "mover"; arquivos `.craftdriver-parcial-*` são apagados (o
original está no cache). O resto (edições, novos) segue a tabela abaixo.

Cada mudança feita na pasta real com o app fechado é tratada assim:

| O que mudou | O que o app faz |
|---|---|
| Arquivo editado (tamanho/data diferente e conteúdo diferente do cache) | Conteúdo vai para o cache, item `Dirty`, envio na fila |
| Arquivo ou pasta novos dentro de uma pasta conhecida | Item novo + `mkdir`/envio. Se o nome já existir: nome de conflito + registro em Conflitos |
| Arquivo movido/renomeado (mesmo conteúdo de um fixado que sumiu do lugar original, mesma drive) | Operação `move`, sem duplicar |
| Arquivo ou pasta apagados (dentro de pasta fixada ou o próprio fixado) | **Nada é apagado na nuvem por isso.** Vira pendência (tabela `offline_deletions`) e o app pergunta ao usuário (ver abaixo). O fixado continua fixado |
| Fora de pasta conhecida (ex.: na raiz), em compartilhado só leitura, link simbólico, erro | Movido para `<montagem> (recuperados)/<data>/…` + erro em Atividade |

Depois disso, as cópias saem da pasta real e o manifesto é apagado, então a montagem volta a ficar sobre uma pasta
vazia. Se a pasta estiver montada por outro processo, nada é importado: tenta de novo na próxima abertura.

## Sobras sem manifesto (`OfflineCopies.MoveAsideLeftovers`, antes de montar)

Se depois da importação a pasta real ainda não estiver vazia (cópias de um encerramento da 0.4.1 interrompido, que
gravava o manifesto só no fim, ou algo posto lá com o app fechado), o conteúdo vai para
`<montagem> (recuperados)/<data>` com um erro em Atividade, e a montagem sobe. Antes o FUSE recusava a pasta ("não
está vazio") e o app ficava sem montagem. Só na montagem real (`AppHost.MountWithFuse`); pasta montada nunca é mexida.

## Apagados com o app fechado: o app pergunta

Ausência local detectada com o app fechado nunca apaga nada na nuvem sozinha. Cada item que sumiu das cópias vira uma
pendência em `offline_deletions` (lid, drive, caminho, quando foi detectado) e conta em `OfflineImportResult.PendingDeletes`.
Uma pasta apagada inteira vira **uma** pendência só quando tudo o que há nela estava nas cópias (o usuário viu o que
apagou). Se havia algo que não estava nelas (não baixado, só local), cada arquivo ausente vira uma pendência própria.

Enquanto o usuário não responde:

- o item continua na nuvem, na montagem e fixado;
- no próximo encerramento ele **não** volta nas cópias (nem o conteúdo de uma pasta pendente);
- a pergunta reaparece na próxima abertura.

API (`Core/Sync/OfflineDeletions.cs`), usada pelo app na abertura depois de o motor iniciar:

- `Pending(engine)`: lista as pendências cujo item ainda existe. As outras são descartadas.
- `ConfirmDelete(engine, lids)`: apaga pelo mesmo caminho do `rm` na montagem (op `Delete` na fila, e a nuvem manda
  para a **Lixeira**) e limpa as pendências. Devolve quantos apagou. Item que sumiu da nuvem nesse meio tempo é
  descartado sem erro.
- `Keep(engine, lids)`: mantém na nuvem e limpa as pendências. O arquivo volta a ser copiado no próximo encerramento.

## Limites

- Só vale para encerramento normal (inclusive desligar o PC, pela trava do logind). Se o app travar ou for morto antes
  (`kill -9`, queda de energia), as cópias não são gravadas, ou ficam pela metade e são limpas na abertura seguinte. Não há gravação contínua das cópias com o app
  aberto (fica para depois, se valer o custo).
- Arquivos somente online não aparecem com o app fechado. Não existe marcador de "arquivo na nuvem".
- Apagar com o app fechado só vale depois de o usuário confirmar na abertura. Até lá o item continua na montagem.

Testes: `tests/CraftDriver.Tests/OfflineCopiesTests.cs` (inclusive queda em cada etapa da cópia e sobras sem
manifesto), `tests/CraftDriver.UiTests/ShutdownInhibitorTests.cs` (logind falso num dbus-daemon privado, com o
descritor da trava passando de verdade) e `AnaliseR4OfflineTests` (sem FUSE: a "montagem" é uma pasta comum, e o "reabrir" usa
o mesmo banco/cache com `Harness(configure, dir, remote, beforeStart)`).
