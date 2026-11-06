# Atualizações

O craftDriver avisa quando sai uma versão nova e, quando dá, se atualiza sozinho: baixa o pacote certo para o jeito
como foi instalado, confere o SHA256 e instala. Código: `src/CraftDriver.Core/Updates/` (o que não depende da
interface) e `src/CraftDriver.App/Services/Update*.cs` + `UserInstallUpdate.cs`.

## Verificação

- `GET https://api.github.com/repos/Kaike-png/craftDriver/releases/latest` com `User-Agent: craftDriver/<versão>`,
  `Accept: application/vnd.github+json` e prazo de 15 s (`UpdateCheck.FetchLatestAsync`). O JSON é lido por
  source generator (`UpdateJsonContext` em `Config/JsonContexts.cs`; o pacote é publicado com trimming).
- A tag `vX.Y.Z` é comparada com a versão do assembly (`AppVersion.Current`, `InformationalVersion` sem `+hash`)
  por `SemVer`: numérica (0.10 > 0.9), sufixos da versão atual (`-dev`, `+abc`) descartados. Release marcada como
  rascunho/pré-lançamento, ou tag com sufixo (`v1.0.0-rc1`), nunca é oferecida.
- Agenda (`UpdateService.Start`): 2 min depois de iniciar e depois a cada 24 h. Cada disparo confere
  `CheckForUpdates` (desligar/religar nas Configurações vale sem reiniciar) e a variável
  `CRAFTDRIVER_NO_UPDATE_CHECK=1` (testes; pacotes de distribuição que se atualizam pelo sistema).
- Falha de rede/HTTP na verificação automática é silenciosa (só `DEBUG` no log); tenta de novo no próximo ciclo.
  "Procurar agora" (Configurações) e `craftdriver update --check` verificam mesmo com a automática desligada e
  mostram o erro.
- "Ignorar esta versão" grava `SkippedUpdateVersion`: essa versão (e mais velhas) não aparece mais; uma mais nova,
  sim. "Procurar agora" mostra até a ignorada (pedido explícito) e esquece o "ignorar".
- Notificação de desktop **uma vez por versão** (`NotifiedUpdateVersion`, gravado — não repete a cada início), com
  "Atualizar agora" e "Ver novidades"; servidor de notificações sem botões (ou `notify-send`) recebe a dica no texto
  ("abra o craftDriver… ou rode `craftdriver update`"). Sem janela (`--headless`), só "Ver novidades".
- `CRAFTDRIVER_UPDATE_URL` troca a base da API (só para testes: GitHub falso local). As URLs dos arquivos vêm do
  próprio JSON da release.

## Jeito de instalação (`InstallDetector`)

| Caso | Como é detectado | Pacote baixado | Instalação |
|---|---|---|---|
| Arch (`craftdriver-bin`) | `pacman -Qqo <exe>` | `craftdriver-bin-X-1-x86_64.pkg.tar.zst` | `pkexec pacman -U --noconfirm` |
| Debian/Ubuntu | `dpkg -S <exe>` → `craftdriver` | `craftdriver_X-1_{amd64,arm64}.deb` | `pkexec apt-get install -y` (sem apt-get: `dpkg -i`) |
| Fedora/openSUSE | `rpm -qf --qf %{NAME} <exe>` | `craftdriver-X-1.{x86_64,aarch64}.rpm` | `pkexec dnf install -y` (senão `zypper --non-interactive install --allow-unsigned-rpm`, senão `rpm -U`) |
| `install-user.sh` | executável sob `~/.local/opt/craftdriver/` | `craftdriver-X-linux-{x64,arm64}.tar.gz` | `install-user.sh` do tarball, desacoplado (abaixo) |
| Outro | `dotnet run`, tarball solto, pacote de terceiros (ex.: `craftdriver-git`) | — | só avisa, com o link da release e o comando sugerido |

- O executável é resolvido pelo link (`/usr/bin/craftdriver` → `/usr/lib/craftdriver/craftdriver`) antes de perguntar
  o dono. Os comandos rodam por `ICommandRunner` (`ProcessRunner` com `LC_ALL=C`; programa ausente = 127), trocado
  nos testes.
- Arquitetura por `RuntimeInformation.ProcessArchitecture` (x64/arm64). Sem arquivo para a combinação — hoje o
  **Arch arm64** (o CI não gera `.pkg.tar.zst` aarch64) — cai para "só avisar" com o comando sugerido. Revisão de
  pacote diferente de `-1` também é aceita.

## Download e conferência (`UpdateDownloader`)

- Destino: `~/.cache/craftdriver/updates/<versão>/` (`AppPaths.UpdatesDir`, dentro do cache **padrão**, não do
  `CacheDir` configurável — o cache de conteúdo tem limpeza e contagem próprias; com `CRAFTDRIVER_HOME`,
  `<home>/cache/updates`). Pastas de outras versões são apagadas a cada download.
- Primeiro baixa o `SHA256SUMS` da release (sem ele, aborta antes de baixar o pacote), depois o pacote para `.part`
  calculando o SHA256 enquanto grava. Hash diferente, arquivo fora do `SHA256SUMS`, HTTP de erro ou queda: apaga o
  `.part`/arquivo e mostra o erro ("não confere", "não lista"…). Nada é instalado.
- O progresso (bytes/total) aparece no painel da Visão geral.

## Instalação

- **Pacote do sistema**: roda o `pkexec` com o app **aberto** (o agente de polkit da sessão pede a senha).
  - código 0 → "Versão X instalada" e a pergunta "Reiniciar agora?". "Sim" segue o mesmo fluxo do **Reiniciar agora**
    (`ConfirmQuitAsync` — pergunta se há transferências — e só então `AppRestart.ScheduleRelaunch`); "Não" deixa o
    botão **Reiniciar agora** no painel e na barra de mensagens.
  - 126/127 (senha cancelada/não autorizado) → "Atualização cancelada"; o app continua na versão atual e pode tentar
    de novo.
  - outro código → erro com o fim da saída do gerenciador e o comando para rodar à mão.
  - sem `pkexec` no sistema → o pacote fica baixado e conferido e o painel mostra o comando (`sudo pacman -U …`).
- **Instalação do usuário**: depois do download, a mesma confirmação do Sair (`ConfirmQuitAsync`); então
  `UserInstallUpdate.Launch` inicia um `sh` com `setsid` (sobrevive ao fim do app e ao SIGHUP do terminal) que:
  1. extrai o tarball numa pasta temporária;
  2. roda o `install-user.sh` **do tarball** — ele mesmo encerra o app (`craftdriver quit`) e espera ele sair e
     soltar a trava antes de trocar `~/.local/opt/craftdriver` (sem senha);
  3. se o app saiu, reabre com o mesmo comando e argumentos do **Reiniciar agora** (`DesktopIntegration.ExecArgv`,
     sem `--minimized`, sem as variáveis de escala), conferindo o PID e a hora de início como o `AppRestart`;
  4. se o instalador falhou antes de encerrar o app, nada é reaberto: o app vê o código de saída do `sh` e mostra o
     erro com o caminho do log (`updates/<versão>/install.log`).
- Só uma atualização por vez (`UpdateService.IsBusy`); botões, bandeja e IPC respeitam.

## Interface

- **Visão geral**: painel "Nova versão X disponível" com o resumo das notas (`UpdateCheck.Summarize`: texto simples,
  sem negrito/código/links e sem a tabela "Instalar" que o CI acrescenta), **Atualizar agora**, **Ver novidades**
  (abre o `html_url`) e **Ignorar esta versão**. Progresso, erro (destacado) e "rode no terminal" aparecem no mesmo
  painel; depois de instalar, **Reiniciar agora**.
- **Configurações ▸ Atualizações**: versão instalada, "Procurar atualizações automaticamente" (grava na hora) e
  **Procurar agora** com o resultado da última verificação.
- **Bandeja**: item "Atualizar para X" quando há versão nova (abre a janela e inicia o fluxo).

## Linha de comando

- `craftdriver update --check`: funciona sem instância; imprime versão instalada, jeito de instalação e a versão
  nova. Saída 0 = atualizado, 10 = há versão nova, 1 = erro.
- `craftdriver update`: com o app aberto, pede a ele pelo IPC (`update`) o mesmo fluxo do botão (a janela aparece).
  Sem instância — ou com uma `--headless`, que responde `sem-interface` (pkexec sem agente gráfico não pediria a
  senha) — baixa e confere aqui e roda o instalador neste terminal: `sudo` com terminal interativo (o gerenciador
  mostra as perguntas), senão `pkexec`; instalação do usuário roda o `install-user.sh` em primeiro plano.

## Testes

- `UpdateCoreTests` (Tests): JSON real da release v0.2.0 (`Fixtures/github-release-latest.json`), versões, escolha
  do pacote por jeito × arquitetura, detecção com executor falso, download com handler falso (SHA256 certo, errado,
  ausente, sem `SHA256SUMS`, 404), comandos de cada gerenciador.
- `UpdateServiceTests`/`UpdateUiTests` (UiTests): agenda (atraso, intervalo, configuração, variável), ignorar,
  notificação uma vez por versão, pkexec 0/1/126/127, sem pkexec, arm64, concorrência, painel/Configurações/bandeja
  em janela headless (PNGs `atualizacao-*.png` e `configuracoes-atualizacoes.png`), IPC.
- `UpdateProcessTests`: o binário real (`update --check` e `update`) isolado contra um `HttpListener` local, e o
  instalador desacoplado com HOME/CRAFTDRIVER_HOME/XDG_RUNTIME_DIR descartáveis e um tarball falso com o
  `install-user.sh` de verdade.
- O módulo `NoUpdateCheck` dos UiTests define `CRAFTDRIVER_NO_UPDATE_CHECK=1` para o processo de teste e os filhos:
  nenhum teste de interface consulta o GitHub (provado com `unshare -rn`).
