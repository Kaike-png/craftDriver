# Início, janela principal e bandeja

## Início sem travar no chaveiro

- `Program.Main` só lê o `config.json` (rápido) e sobe o Avalonia. O `AppHost` — cujo `AuthService` espera o
  chaveiro por até 60 s no começo da sessão — é criado em segundo plano por `App.StartAsync`
  (`StartupCoordinator<AppHost>`).
- Se demorar mais de 400 ms, aparece a `SplashWindow` ("Abrindo o chaveiro do sistema…") com
  **Continuar sem o chaveiro** (cancela a espera: mesmo caminho do fim do prazo de 60 s — tokens em arquivo nesta
  execução, `KeyringUnavailable` se havia tokens no chaveiro; a preferência gravada em `token-storage` não muda) e
  **Sair**/Esc/fechar (encerra o processo sem esperar a fábrica).
- `--minimized` com contas e com host de bandeja: nenhuma janela; o ícone aparece já ("iniciando…") e o clique
  mostra a espera. Sem host de bandeja, a janela abre (`StartupPlan.ShowWindowAtStart`).
- `--headless` e os comandos de CLI continuam sem janela (o headless espera o chaveiro como antes).
- Um chaveiro travado (ex.: pedindo senha) que termine de abrir depois de "Continuar" é descartado: o registro do
  helper no cache do MSAL só acontece depois do `await` cancelável (`AuthService.KeyringAccess`).
- Instância única: `Program.Main` pega um `flock` em `run/craftdriver.lock` e abre o socket de IPC **antes** do
  Avalonia e do `AppHost`. Abrir o app de novo durante a espera do chaveiro não inicia outra instância: o segundo
  processo fala com o primeiro (que responde "ainda iniciando" enquanto o `AppHost` não existe) e sai.

## Fechar a janela

- Com host StatusNotifier (`org.kde.StatusNotifierWatcher` com `IsStatusNotifierHostRegistered = true`,
  consultado na hora de fechar — `TrayHostProbe`): esconde; na primeira vez, notificação "O CraftDriver continua em
  execução" (`TrayNoticeShown` no config).
- Sem host: **não esconde** — diálogo `NoTrayCloseWindow` com Encerrar / Minimizar / Cancelar (Esc). Encerrar
  desmonta a pasta. Minimizar não tem efeito em compositores que não minimizam (Hyprland): a janela fica visível.

## Tamanho e posição

`WindowWidth/WindowHeight/WindowX/WindowY/WindowMaximized` no config, gravados a cada fechamento (inclusive o que
esconde na bandeja). A posição só é restaurada se metade da faixa do título cair na área útil de alguma tela atual.
No Wayland o compositor decide a posição: sob XWayland o Hyprland pode ignorar o pedido (janela lado a lado);
tamanho e maximizado continuam valendo.

## Escala (HiDPI) sob XWayland

O CraftDriver usa só o backend X11 do Avalonia (`UsePlatformDetect`), ou seja, XWayland numa sessão Wayland.

- Avalonia 12.1.5 (csproj). O backend Wayland nativo existe desde o 12.1 (pacote `Avalonia.Wayland`), mas é
  **experimental**, não é escolhido pelo `UsePlatformDetect()` e ainda não tem menu global/ícone de janela do KDE:
  https://avaloniaui.net/blog/release-12-1 , https://docs.avaloniaui.net/docs/platform-specific-guides/linux .
  Adotá-lo é uma dependência nova: não foi feito.
- Como o X11 do Avalonia escolhe a escala (fonte da tag 12.1.5,
  https://github.com/AvaloniaUI/Avalonia/blob/12.1.5/src/Avalonia.X11/Screens/X11Screens.Scaling.cs):
  `AVALONIA_SCREEN_SCALE_FACTORS`/`AVALONIA_GLOBAL_SCALE_FACTOR`/`AVALONIA_USE_PHYSICAL_DPI` → `QT_*` equivalentes
  (salvo `AVALONIA_SCREEN_SCALE_IGNORE_QT=1`) → `Xft.dpi`/96. `GDK_SCALE` é ignorado (medido abaixo).
- Hyprland (https://wiki.hypr.land/configuring/extra/xwayland/): com `xwayland:force_zero_scaling = false`
  (padrão) apps X11 em monitor com escala são ampliados pelo compositor → borrados; com `true` ficam nítidos mas
  sem escala — o wiki sugere `GDK_SCALE`, que o Avalonia não lê.
- Mitigação (`DisplayScaling`, antes do `AppBuilder`): em XWayland + Hyprland com `force_zero_scaling = true` e
  algum monitor com escala ≠ 1, define `AVALONIA_SCREEN_SCALE_FACTORS=<saída>=<escala>;…` lendo `j/monitors` do
  soquete do Hyprland. Com `force_zero_scaling = false` não mexe (somaria com a ampliação) e registra no log a
  dica de ligar a opção. Variáveis definidas pelo usuário prevalecem; `CRAFTDRIVER_AUTO_SCALE=0` desliga.
- Medido nesta máquina (Hyprland, 2 monitores 1920×1080 escala 1, sem `Xft.dpi`, `force_zero_scaling=false`), com
  um executável mínimo Avalonia 12.1.5 que lê `Screens.All` sem mostrar janela em `DISPLAY=:1`:
  sem variáveis → `eDP-1` e `HDMI-A-1` com escala 1; `AVALONIA_SCREEN_SCALE_FACTORS=eDP-1=1.5;HDMI-A-1=1` → 1.5 e 1
  (os nomes do RandR do XWayland são os mesmos do `hyprctl monitors`); `GDK_SCALE=2` → 1 e 1 (ignorado);
  `AVALONIA_GLOBAL_SCALE_FACTOR=2` → 2 e 2.
- **Não verificado**: o borrado em si (não há monitor HiDPI aqui; mudar a escala do monitor do usuário estava fora
  do combinado) e o resultado visual com `force_zero_scaling = true` + escala fracionária.
