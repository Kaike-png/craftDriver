# Cópias offline dos itens fixados (app encerrado)

No Windows, o OneDrive guarda os arquivos no próprio NTFS. Um filtro do kernel (Cloud Files API) continua ativo
mesmo com o app fechado, então os itens "Sempre manter neste dispositivo" seguem na pasta. No Linux, `~/OneDrive` é
uma montagem FUSE servida pelo processo do craftDriver. Ao **Encerrar**, a montagem sai e aparece a pasta real que
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
  - qualquer caminho que já exista na pasta real (nunca sobrescreve).
- O manifesto vai para a tabela `offline_copies` (caminho, lid, tamanho, mtime em ticks).

## Ao abrir (`AppHost.Start` → `OfflineCopies.Import`, ANTES de `SyncEngine.Start`)

A ordem é obrigatória, e o `Import` recusa se o motor já tiver drives. Com o delta rodando antes, um arquivo editado
nos dois lados adotaria a versão da nuvem, e o envio seguinte a sobrescreveria. Como a importação vem antes, o item
fica `Dirty` com o eTag antigo, e o envio recebe 412, o que gera a **cópia de conflito** de sempre.

Cada mudança feita na pasta real com o app fechado é tratada assim:

| O que mudou | O que o app faz |
|---|---|
| Arquivo editado (tamanho/data diferente e conteúdo diferente do cache) | Conteúdo vai para o cache, item `Dirty`, envio na fila |
| Arquivo ou pasta novos dentro de uma pasta conhecida | Item novo + `mkdir`/envio. Se o nome já existir: nome de conflito + registro em Conflitos |
| Arquivo movido/renomeado (mesmo conteúdo de um fixado que sumiu do lugar original, mesma drive) | Operação `move`, sem duplicar |
| Arquivo ou pasta apagados | **Nada é apagado na nuvem.** Se o item apagado era o fixado (não um filho), deixa de estar fixado |
| Fora de pasta conhecida (ex.: na raiz), em compartilhado só leitura, link simbólico, erro | Movido para `<montagem> (recuperados)/<data>/…` + erro em Atividade |

Depois disso, as cópias saem da pasta real e o manifesto é apagado, então a montagem volta a ficar sobre uma pasta
vazia. Se a pasta estiver montada por outro processo, nada é importado: tenta de novo na próxima abertura.

## Limites

- Só vale para encerramento normal. Se o app travar ou for morto, as cópias não são gravadas e a pasta fica vazia,
  como antes.
- Arquivos somente online não aparecem com o app fechado. Não existe marcador de "arquivo na nuvem".
- Se um arquivo de dentro de uma pasta fixada for apagado com o app fechado, ele volta na próxima vez: a pasta
  continua fixada.

Testes: `tests/CraftDriver.Tests/OfflineCopiesTests.cs` (sem FUSE: a "montagem" é uma pasta comum, e o "reabrir" usa
o mesmo banco/cache com `Harness(configure, dir, remote, beforeStart)`).
