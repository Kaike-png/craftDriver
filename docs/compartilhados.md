# "Compartilhados comigo" e atalhos (itens de outras drives)

## O que aparece no Linux

```
~/OneDrive/
├── OneDrive - Pessoal/                       ← drive do usuário
│   └── Trabalho/
│       └── Projeto do Dono/                  ← atalho ("Adicionar a Meus arquivos"): fica onde foi colocado
│           ├── a.txt                         ← conteúdo vem da drive do dono
│           └── Sub/b.txt
└── OneDrive - Pessoal - Compartilhados comigo/   ← pasta virtual da conta (/me/drive/sharedWithMe)
    ├── Relatorio.txt
    └── Fotos/f.jpg
```

- **Atalhos** (driveItem com `remoteItem` no delta da drive do usuário) viram pastas/arquivos navegáveis no lugar
  do atalho. Renomear/mover o atalho na nuvem move a pasta aqui; apagar o atalho (ou a pasta que o contém) faz ela
  sumir.
- **"Compartilhados comigo"**: uma pasta virtual por conta, só para as drives principais (pessoal/empresa; bibliotecas
  do SharePoint não têm). Nomes repetidos (dois "Relatório.docx" de pessoas diferentes) ganham " (2)".

### Por que a pasta "Compartilhados comigo" é irmã da drive, e não está dentro dela

1. **Sem colisão com conteúdo real.** Dentro da drive, uma pasta de verdade chamada "Compartilhados comigo" (ou que
   o usuário crie depois) ficaria escondida ou duplicada. Na raiz da montagem só existem pastas criadas pelo
   CraftDriver.
2. **Semântica de escrita clara.** A raiz da montagem já é virtual e não aceita arquivos; dentro da drive o usuário
   esperaria poder criar arquivos ao lado, e o delta/varredura da drive (Sweep, conflitos de nome, exclusão em massa)
   teria de tratar um nó que não existe na nuvem.
3. **Agrupamento por conta continua visível:** o nome é `<pasta da drive> - Compartilhados comigo`, que ordena logo
   abaixo da pasta da conta no `ls`/gerenciador de arquivos.

## Representação no SQLite: uma "sub-drive" por raiz compartilhada

Cada raiz (atalho ou entrada de sharedWithMe) vira uma **sub-drive interna** que reaproveita o motor (`DriveSync`):

- Tabela `shared_roots(key, host_drive, source, host_item, remote_drive, remote_item, root_lid, writable)`.
- `key` é o `drive_id` interno dos itens dela: `<drive>|atalho|<id do atalho>` ou
  `<drive>|comigo|<driveId do dono>!<id do item>`. Ids da Graph não têm `|` (`SyncEngine.IsSharedKey`).
  Assim o índice único `(drive_id, item_id)` continua valendo mesmo que o mesmo item apareça por atalho e por
  sharedWithMe (são duas cópias independentes, com cache separado).
- A linha raiz da sub-drive tem `parent_lid` = pasta onde está o atalho (ou a pasta virtual), então `Resolve`/`ReadDir`
  do FUSE andam pela árvore sem saber que mudou de drive. A pasta virtual também é uma linha em `items`
  (`drive_id = <drive>|comigo`, `parent_lid = 0`) com registro em `drives`.
- `DriveSync.Shared` != null marca a sub-drive; `ApiDriveId` = drive do dono, usado em **todas** as chamadas à Graph
  (download, upload, mkdir, move, delete, settime). A fila de operações (`ops.drive_id = key`) é própria, então uma
  falha de permissão no compartilhamento não trava a fila da drive do usuário.
- Sub-drives não entram em `SyncEngine.Drives` (não viram pasta na raiz, nem contam no status/ícone da bandeja).
  `GetDrive(key)` as encontra (download sob demanda, fixar, "Tentar de novo").
- `ReconcileShared()` liga/desliga as sub-drives conforme `shared_roots` (após cada delta, após cada consulta a
  sharedWithMe e ao recarregar drives). Ao sumir a linha, o cache dela é apagado.

### Por que não "listagem sob demanda com cache curto"

`lookup`/`readdir` respondem inline na thread do loop do FUSE (ver `references/fuse-lowlevel.md`); ir à rede ali
trava o sistema de arquivos inteiro e não funciona offline. Com a sub-drive o FUSE só lê o banco; a rede fica nos
loops do motor. Offline, as pastas mostram a última listagem (ou ficam vazias se nunca foram listadas), sem travar.

## Atualização: sem delta fora da raiz

A documentação oficial do delta (v1.0) só define `GET /drives/{drive-id}/root/delta` (e equivalentes `/me`, `/sites`,
`/groups`, `/users`) — **não há delta documentado para uma pasta**. Na prática o OneDrive pessoal aceita
`/drives/{id}/items/{id}/delta`, mas o OneDrive for Business/SharePoint só aceita na raiz, e sem acesso à raiz da drive
do dono não dá para usá-lo. Por isso a sub-drive **lista a pasta inteira** (`GET /drives/{dono}/items/{id}/children`,
paginado por `@odata.nextLink`, BFS) a cada `DriveSync.SharedPollSeconds` (300 s; "Sincronizar agora" antecipa) e
aplica o resultado como uma enumeração completa: mesmo `ApplyRemote` do delta, e `Sweep` remove o que sumiu.

- Raiz com 404/403 (compartilhamento removido / sem acesso) → a sub-drive fica vazia (`Inaccessible`).
- Mais de `SharedMaxItems` (50 000) itens: listagem parcial e nada é removido (evita apagar por listagem incompleta).
- Atalhos dentro de pastas compartilhadas e pacotes (OneNote) são ignorados.
- Otimização possível (não feita): delta por pasta no OneDrive pessoal.

"Compartilhados comigo" é consultado pela drive principal no mesmo loop do delta, no máximo a cada
`SharedPollSeconds`. Erro de rede/servidor mantém a lista anterior.

## Permissões

- Ao varrer, a sub-drive pergunta `GET /drives/{dono}/items/{id}/permissions`: para quem não é dono vêm só as
  permissões que valem para ele; papel `write` ou `owner` → gravável. Erro (403/404) ou sem papel de escrita →
  **somente leitura**. Até a primeira verificação, toda sub-drive é somente leitura.
- Somente leitura: arquivos `0444`, pastas `0555` (o FUSE monta com `default_permissions`, então o kernel já recusa) e
  o `VirtualFs` devolve `EACCES` em open para escrita, create, mkdir, unlink, rmdir, rename, truncate e utimens
  (`EPERM` em chmod) **sem enfileirar nada**. A pasta virtual "Compartilhados comigo" é sempre somente leitura.
- Com escrita: editar/criar/renomear/apagar dentro do compartilhamento vai para a drive do dono (`ApiDriveId`), com o
  mesmo tratamento de conflitos (eTag/412) das drives normais. Mover entre o compartilhamento e a drive do usuário
  dá `EXDEV` (o `mv` copia e apaga).
- A raiz do atalho/compartilhamento não pode ser apagada, renomeada nem substituída pelo Linux (`EPERM`): apagar pela
  Graph removeria a pasta **do dono**. Remover o atalho continua sendo pelo site/app do OneDrive.

## Escopos OAuth

Os escopos atuais já bastam: pessoal `Files.ReadWrite.All`; empresa `Files.ReadWrite.All` + `Sites.ReadWrite.All`.
A documentação do sharedWithMe exige `Files.Read.All` (ou superior) e avisa que com `Files.Read`/`Files.ReadWrite` os
itens retornados não ficam acessíveis; `.All` cobre isso. Não há escopo novo, logo **nenhum re-consentimento**.

## Limitações e riscos conhecidos

- **`/me/drive/sharedWithMe` está obsoleta**: a Microsoft avisa que ela opera "degradada" até novembro de 2026 e
  depois para de retornar dados, sem substituto 1:1 anunciado. Na prática ela já pode devolver só parte dos itens (ou
  nenhum), e a pasta "Compartilhados comigo" acompanha o que a API devolver (item que some da resposta some daqui).
  **Atalhos não dependem dela** (vêm do delta da própria drive) — é o caminho recomendado ao usuário: "Adicionar
  atalho a Meus arquivos" no site.
- Itens compartilhados de outros locatários (`?allowexternal=true`) não são pedidos.
- Alterações locais ainda não enviadas dentro de um compartilhamento que deixa de existir (atalho apagado, item
  removido do sharedWithMe) são descartadas, com aviso na Atividade.
- A permissão é verificada por raiz; um item com permissão própria mais restrita dentro de uma pasta gravável falha no
  envio (vai para "Problemas"), como qualquer erro permanente.
- Uma pasta compartilhada grande é relistada inteira a cada 5 min (custo de requisições; sem delta).
- Nada disso foi testado contra contas reais: o formato das respostas segue a documentação (testes de protocolo em
  `SharedGraphProtocolTests`) e o comportamento segue o `FakeGraph` (drive de outro dono com acesso restrito).

## Documentação consultada

- driveItem: delta — https://learn.microsoft.com/en-us/graph/api/driveitem-delta?view=graph-rest-1.0
  (só `/root/delta` nas requisições HTTP; permissões Files.Read…)
- drive: sharedWithMe (deprecated) — https://learn.microsoft.com/en-us/graph/api/drive-sharedwithme?view=graph-rest-1.0
  (aviso de obsolescência, `remoteItem` sempre presente, `remoteItem.parentReference.driveId`, escopos `.All`,
  `allowexternal`)
- remoteItem resource — https://learn.microsoft.com/en-us/graph/api/resources/remoteitem?view=graph-rest-1.0
  ("shared, added to the user's OneDrive"; id e drive do item de origem)
- List permissions — https://learn.microsoft.com/en-us/graph/api/driveitem-list-permissions?view=graph-rest-1.0
  ("For a non-owner caller, only the sharing permissions that apply to the caller are returned")
- List children — https://learn.microsoft.com/en-us/graph/api/driveitem-list-children?view=graph-rest-1.0
- Discussões sobre a falta de substituto do sharedWithMe (Microsoft Q&A):
  https://learn.microsoft.com/en-us/answers/questions/5626735/what-is-the-replacement-for-get-me-drive-sharedwit
