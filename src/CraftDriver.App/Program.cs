using Avalonia;
using CraftDriver.App.Services;
using CraftDriver.Core.Auth;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.Ipc;

namespace CraftDriver.App;

sealed class Program
{
    /// <summary>Trava de instância única (viva até o processo sair).</summary>
    static SingleInstance? _instance;

    /// <summary>
    /// O que fazer com SIGTERM/SIGINT/SIGHUP (systemctl --user stop, fim da sessão, Ctrl+C): encerramento completo
    /// (desmonta, para o motor, grava a configuração/posição, apaga o socket) e saída 0. Null = ainda não há o que
    /// encerrar: o padrão do sistema (terminar) vale.
    /// </summary>
    internal static volatile Action? OnShutdownSignal;
    static readonly List<System.Runtime.InteropServices.PosixSignalRegistration> Signals = [];

    /// <summary>1º sinal = encerramento completo; 2º durante o encerramento = saída imediata (§25.5).</summary>
    static readonly ShutdownSignalGate SignalGate = new(() => OnShutdownSignal, Environment.Exit, DeleteSocket);

    static void RegisterShutdownSignals()
    {
        foreach (var sig in new[] { System.Runtime.InteropServices.PosixSignal.SIGTERM, System.Runtime.InteropServices.PosixSignal.SIGINT, System.Runtime.InteropServices.PosixSignal.SIGHUP })
            Signals.Add(System.Runtime.InteropServices.PosixSignalRegistration.Create(sig, SignalGate.Handle));
    }

    static void DeleteSocket()
    {
        try { File.Delete(AppPaths.SocketPath); } catch { }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        try { AppPaths.MigrateLegacy(); }
        catch (Exception ex) { Console.Error.WriteLine("Aviso: migração da configuração antiga falhou: " + ex.Message); }
        try { AppPaths.EnsureDirs(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("craftDriver: " + ex.Message);
            return 1;
        }

        if (args.Length > 0 && CliCommands.ContainsKey(args[0]))
            return RunCli(args).GetAwaiter().GetResult();
        // atualização: --check funciona sem instância; sem --check, pede à instância ou instala aqui no terminal
        if (args is ["update", ..])
            return UpdateCli.RunAsync(args, Console.Out, Console.Error).GetAwaiter().GetResult();
        if (args.Contains("--version") || args.Contains("-V"))
        {
            Console.WriteLine("craftdriver " + AppVersion.Current);
            return 0;
        }
        // diagnóstico do sistema (TLS: libssl + ca-certificates), usado pelo smoke dos pacotes
        if (args is ["diagnose", ..]) return TlsDiagnostics.Run(args);
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }
        if (args.Length > 0 && args[0] == "login")
            return RunLogin(args).GetAwaiter().GetResult();

        // instância única: a trava vem antes de tudo — enquanto a 1ª espera o chaveiro (até 60 s) ela já é dona do
        // socket; a 2ª só conversa com ela e nunca apaga o socket nem tenta montar o mesmo ponto
        try { _instance = SingleInstance.TryAcquire(AppPaths.LockFile); }
        catch (SingleInstanceException ex)
        {
            // disco cheio, só-leitura, sem permissão: não é "outra instância" — mostra o erro real e sai ≠ 0
            return StartupFailed(args, ex.Message);
        }
        if (_instance == null) return AlreadyRunning(args);

        var headless = args.Contains("--headless");
        IpcServer? early = null;
        try
        {
            // só a configuração (rápido); o AppHost — que espera o chaveiro por até 60 s — é criado depois
            var cfg = AppConfig.Load();
            if (cfg.LoadWarning is { } warning) Console.Error.WriteLine("Aviso: " + warning);
            // a mesma pasta não pode ser montada por duas instâncias com configurações (CRAFTDRIVER_HOME) diferentes
            if (InstanceLocks.AcquireMount(cfg.MountPoint, out var mountInUse) is { } mountLockError)
                return StartupFailed(args, mountLockError, mountInUse ? AlreadyRunningExitCode : 1);

            try
            {
                early = new IpcServer(AppPaths.SocketPath, r => Task.FromResult(StartupIpc.Handle(r, headless ? null : App.RequestStartupShow,
                    headless ? QuitWhileStarting : App.RequestStartupQuit)));
            }
            catch (Exception ex) { Console.Error.WriteLine("Aviso: comandos do craftDriver (IPC) indisponíveis: " + ex.Message); }
            CrashGuard.Install(() => App.Host?.Activity, null);
            // sinal antes de haver o que desmontar (espera do chaveiro, Avalonia subindo): sai já, apagando o socket —
            // antes o término padrão pulava o finally e o socket ficava (§22.9)
            OnShutdownSignal = ExitWhileStarting;
            RegisterShutdownSignals();
            DesktopIntegration.RemoveLegacyLeftovers();

            if (headless)
            {
                // sem interface: espera o chaveiro aqui mesmo, como antes (nenhuma janela)
                App.Host = AppHost.CreateAsync(cfg, null, CancellationToken.None).GetAwaiter().GetResult();
                return RunHeadless(App.Host, early);
            }

            App.StartupConfig = cfg;
            App.EarlyIpc = early;
            App.StartMinimized = args.Contains("--minimized") || cfg.StartMinimized;
            // antes do AppBuilder: o backend X11 do Avalonia lê as variáveis de escala ao iniciar
            var scaling = DisplayScaling.Apply();
            App.ScalingNote = scaling.Note;
            // a interface sobe já; o AppHost é criado em segundo plano (App.StartAsync, com janela de espera)
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            return 0;
        }
        finally
        {
            OnShutdownSignal = null;
            early?.Dispose(); // normalmente já fechado pelo AppHost; saiu antes dele (espera abandonada): apaga o socket
            InstanceLocks.Release();
            _instance.Dispose();
        }
    }

    /// <summary>Não dá para iniciar (trava): mensagem no terminal e, aberto pelo menu (sem terminal), notificação.</summary>
    static int StartupFailed(string[] args, string message, int exitCode = 1)
    {
        Console.Error.WriteLine("craftDriver: " + message);
        if (!args.Contains("--headless"))
            DesktopIntegration.Notify("craftDriver não iniciou", message).Wait(TimeSpan.FromSeconds(5));
        return exitCode;
    }

    /// <summary>Sinal antes de haver AppHost/interface: nada montado nem gravado; apaga o socket e sai 0.</summary>
    static void ExitWhileStarting()
    {
        DeleteSocket();
        Environment.Exit(0);
    }

    /// <summary>
    /// "quit" enquanto o --headless espera o chaveiro: nada montado nem gravado ainda; sai depois de a resposta ir
    /// (a espera do chaveiro bloqueia a thread principal, então não há um laço para avisar).
    /// </summary>
    static void QuitWhileStarting() => _ = Task.Delay(300).ContinueWith(_ =>
    {
        try { File.Delete(AppPaths.SocketPath); } catch { }
        Environment.Exit(0);
    }, TaskScheduler.Default);

    /// <summary>Outra instância tem a trava: pede para ela aparecer (se for o caso) e sai.</summary>
    static int AlreadyRunning(string[] args)
    {
        // a dona da trava pode estar entre pegar a trava e abrir o socket: tenta por alguns segundos
        IpcResponse? running = null;
        for (var i = 0; i < 10 && running == null; i++)
        {
            running = IpcClient.SendAsync(AppPaths.SocketPath, "ping", [], TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            if (running == null) Thread.Sleep(500);
        }
        if (running is { Ok: true } && !args.Contains("--headless") && !args.Contains("--minimized"))
        {
            var shown = IpcClient.SendAsync(AppPaths.SocketPath, "show", []).GetAwaiter().GetResult();
            if (shown is { Ok: false })
            {
                // aberto pelo menu: sem terminal para ver a mensagem, então notifica também
                Console.Error.WriteLine(shown.Text);
                DesktopIntegration.Notify("craftDriver", shown.Text).Wait(TimeSpan.FromSeconds(5));
                return 1;
            }
        }
        var text = running?.Text == "iniciando"
            ? "O craftDriver já está em execução (ainda iniciando)."
            : "O craftDriver já está em execução.";
        if (args.Contains("--headless"))
        {
            // serviço do systemd (ou script) com outra instância viva: sair 0 calado deixava a unit "inactive" e o
            // "systemctl --user stop" nunca chegava à instância de verdade. Código próprio (sem reinício: a unit tem
            // RestartPreventExitStatus) e a mensagem no stderr, que vai para o journal.
            Console.Error.WriteLine(text + " Este --headless não iniciou (a trava " + AppPaths.LockFile + " é da outra instância).");
            return AlreadyRunningExitCode;
        }
        Console.WriteLine(text);
        return 0;
    }

    /// <summary>Saída do --headless quando outra instância já tem a trava (a unit do systemd não reinicia com ele).</summary>
    internal const int AlreadyRunningExitCode = 3;

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    static void PrintHelp()
    {
        Console.WriteLine("""
            craftDriver — cliente para OneDrive e SharePoint com arquivos sob demanda

            Uso:
              craftdriver                 abre a interface (fica na bandeja)
              craftdriver --minimized     inicia direto na bandeja
              craftdriver --headless      roda sem interface (servidor)
              craftdriver --version       mostra a versão
              craftdriver diagnose --tls  confere OpenSSL, certificados raiz e HTTPS (sem conta)
              craftdriver login [--personal|--work]   adiciona conta pelo terminal
              craftdriver update --check  procura versão nova (0 = atualizado, 10 = há versão nova, 1 = erro)
              craftdriver update          atualiza: com o app aberto, pela janela dele; senão, aqui (pede a senha
                                          de administrador para pacotes do sistema)

            Com o app em execução:
              craftdriver status [arquivos…]   status geral ou de itens
              craftdriver pin <arquivos…>      sempre manter neste dispositivo
              craftdriver unpin <arquivos…>    liberar espaço (somente online)
              craftdriver free <arquivos…>     remove do cache sem mudar a fixação
              craftdriver exclude <pastas…>    para de sincronizar a pasta aqui (some da montagem; fica na nuvem)
              craftdriver include <pastas…>    volta a sincronizar uma pasta excluída
              craftdriver deletes [confirm|restore]   exclusão em massa retida
              craftdriver retry                tenta de novo o que falhou (lista em "status")
              craftdriver open                 abre a pasta sincronizada no gerenciador de arquivos
              craftdriver share [--edit] <arquivos…>   cria link de compartilhamento e copia (--edit: pode editar)
              craftdriver versions <arquivo> [--list | --restore=ID | --copy=ID]   versões anteriores
              craftdriver trash [restore N | open]    excluídos recentes / restaurar da Lixeira
              craftdriver sync | pause | resume
              craftdriver quit                 encerra e espera o app terminar (até 30 s; CRAFTDRIVER_QUIT_TIMEOUT)
            """);
    }

    /// <summary>Como cada comando do CLI monta os argumentos enviados à instância em execução.</summary>
    internal enum CliArgs
    {
        /// <summary>Sem argumentos (o que vier é ignorado).</summary>
        None,
        /// <summary>Caminhos (viram absolutos); --notify sai.</summary>
        Paths,
        /// <summary>Palavras como vieram (ex.: deletes confirm, trash restore 3); --notify sai.</summary>
        Words,
        /// <summary>Caminhos absolutos e opções --x mantidas (inclusive --notify, tratado pela instância).</summary>
        PathsAndFlags,
    }

    /// <summary>Comandos do CLI (instância em execução, via IPC) e o formato dos argumentos de cada um.</summary>
    internal static readonly Dictionary<string, CliArgs> CliCommands = new()
    {
        ["pin"] = CliArgs.Paths, ["unpin"] = CliArgs.Paths, ["free"] = CliArgs.Paths, ["status"] = CliArgs.Paths,
        ["exclude"] = CliArgs.Paths, ["include"] = CliArgs.Paths,
        ["sync"] = CliArgs.None, ["pause"] = CliArgs.None, ["resume"] = CliArgs.None, ["quit"] = CliArgs.None,
        ["retry"] = CliArgs.None, ["open"] = CliArgs.None,
        ["deletes"] = CliArgs.Words, ["trash"] = CliArgs.Words,
        ["share"] = CliArgs.PathsAndFlags, ["versions"] = CliArgs.PathsAndFlags,
    };

    /// <summary>Argumentos que vão para a instância (args[0] = comando).</summary>
    internal static string[] BuildCliArgs(string[] args)
    {
        var rest = args.Skip(1);
        return CliCommands[args[0]] switch
        {
            CliArgs.None => [],
            CliArgs.Paths => rest.Where(a => a != "--notify").Select(Path.GetFullPath).ToArray(),
            CliArgs.Words => rest.Where(a => a != "--notify").ToArray(),
            _ => rest.Select(a => a.StartsWith("--") ? a : Path.GetFullPath(a)).ToArray(),
        };
    }

    static async Task<int> RunCli(string[] args)
    {
        var notify = args.Contains("--notify");
        // "open" vem do menu do atalho (sem terminal): avisa por notificação quando falha, nunca quando dá certo
        var notifyErrors = notify || args[0] is "open" or "versions";
        var share = args[0] == "share";
        var rest = BuildCliArgs(args);
        // share: se a instância não tiver a área de transferência (serviço do systemd), este processo copia
        if (share && ClipboardService.Plan(Environment.GetEnvironmentVariable).Count > 0) rest = [.. rest, CliShare.CliCopiesFlag];
        var resp = await IpcClient.SendAsync(AppPaths.SocketPath, args[0], rest, TimeSpan.FromMinutes(2));
        if (resp == null)
        {
            const string msg = "O craftDriver não está em execução. Abra o aplicativo primeiro.";
            if (notifyErrors) await NotifyAndWait(msg);
            Console.Error.WriteLine(msg);
            return 2;
        }
        var text = resp.Text;
        // share --notify: a notificação (com "Abrir no navegador") sai da instância em execução, não daqui —
        // a não ser que a cópia tenha ficado para cá (aí a instância não notificou)
        var hostNotifies = notify && share && resp.Links == null;
        if (resp.Links != null) text = await CliShare.FinishAsync(resp, t => ClipboardService.CopyAsync(t));
        if (!hostNotifies && (notify || (notifyErrors && !resp.Ok))) await NotifyAndWait(text);
        Console.WriteLine(text);
        // quit: a resposta sai antes do encerramento (desmontar, terminar envios). Quem chama em seguida (instalador,
        // desinstalador, "Reiniciar") precisa da instância fora: espera ela soltar a trava
        if (args[0] == "quit" && resp.Ok) return await WaitInstanceExitAsync(QuitWaitTimeout()) ? 0 : 1;
        return resp.Ok ? 0 : 1;
    }

    /// <summary>Prazo do "craftdriver quit" (CRAFTDRIVER_QUIT_TIMEOUT em segundos; padrão 30).</summary>
    static TimeSpan QuitWaitTimeout() =>
        TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("CRAFTDRIVER_QUIT_TIMEOUT"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s >= 0 ? s : 30);

    /// <summary>
    /// Espera a instância soltar a trava de instância única (o processo saiu ou está no fim do encerramento). Pega e
    /// solta na hora: não segura nada que impeça a próxima instância de abrir.
    /// </summary>
    internal static async Task<bool> WaitInstanceExitAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                using var probe = SingleInstance.TryAcquire(AppPaths.LockFile);
                if (probe != null) return true;
            }
            catch (SingleInstanceException ex)
            {
                Console.Error.WriteLine("Não foi possível conferir se o craftDriver terminou: " + ex.Message);
                return false;
            }
            if (DateTime.UtcNow >= until)
            {
                Console.Error.WriteLine($"O craftDriver recebeu o pedido para sair, mas ainda não terminou depois de {timeout.TotalSeconds:0} s " +
                                        "(desmontagem ocupada por um programa com arquivo aberto na pasta, ou envio em andamento). " +
                                        "Veja \"craftdriver status\" e o log; para forçar, envie SIGTERM duas vezes ao processo.");
                return false;
            }
            await Task.Delay(200);
        }
    }

    /// <summary>Processo curto: espera a notificação ser entregue (D-Bus) antes de sair, por até 5 s.</summary>
    static Task NotifyAndWait(string text) =>
        DesktopIntegration.Notify("craftDriver", text).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });

    /// <summary>
    /// Login pelo terminal. Só autentica (os tokens vão para o cache compartilhado) e não monta um
    /// AppHost: se o app estiver aberto, a conta é entregue a ele por IPC — a instância em execução é
    /// dona do config.json e regravaria por cima de uma alteração feita por fora. Sem instância,
    /// relê o config.json do disco logo antes de gravar.
    /// </summary>
    static async Task<int> RunLogin(string[] args)
    {
        bool personal;
        if (args.Contains("--personal")) personal = true;
        else if (args.Contains("--work")) personal = false;
        else
        {
            Console.Write("Tipo de conta — [1] pessoal (Outlook/Hotmail/Live)  [2] trabalho ou escola: ");
            personal = Console.ReadLine()?.Trim() != "2";
        }
        try
        {
            var loaded = AppConfig.Load();
            if (loaded.LoadWarning is { } warning) Console.Error.WriteLine("Aviso: " + warning);
            var auth = await AuthService.CreateAsync(loaded);
            foreach (var note in auth.StartupNotes) Console.Error.WriteLine(note);
            var acc = personal && !args.Contains("--manual")
                ? await auth.SignInWithSystemBrowserAsync(true, uri =>
                {
                    Console.WriteLine();
                    Console.WriteLine("Abrindo o navegador. Se não abrir, acesse:");
                    Console.WriteLine(uri.AbsoluteUri);
                    Console.WriteLine();
                    Console.WriteLine("Entre com sua conta; o login conclui sozinho quando o navegador mostrar \"Pronto! Login recebido.\".");
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", uri.AbsoluteUri) { UseShellExecute = false }); }
                    catch { }
                    return Task.CompletedTask;
                }, CancellationToken.None)
                : personal
                ? await auth.SignInWithBrowserAsync(true, (authUri, ct) =>
                {
                    Console.WriteLine();
                    Console.WriteLine("1. Abra esta página no navegador e entre com sua conta:");
                    Console.WriteLine(authUri.AbsoluteUri);
                    Console.WriteLine();
                    Console.WriteLine("2. No final o navegador mostra uma página em branco. Cole aqui o endereço inteiro dela:");
                    while (true)
                    {
                        Console.Write("> ");
                        var err = AuthService.ValidatePastedRedirect(Console.ReadLine(), out var uri);
                        if (uri != null) return Task.FromResult(uri);
                        Console.WriteLine(err);
                    }
                }, CancellationToken.None)
                : await auth.SignInWithDeviceCodeAsync(false, (url, code, message) =>
                {
                    Console.WriteLine();
                    Console.WriteLine($"Abra {url} e digite o código: {code}");
                    Console.WriteLine();
                    return Task.CompletedTask;
                }, CancellationToken.None);

            var drive = await new GraphClient((force, ct) => auth.GetTokenAsync(acc.Id, force, ct)).GetMyDriveAsync(CancellationToken.None);
            var payload = IpcJson.Serialize(new AddAccountRequest(acc, drive.Id, drive.WebUrl));
            var resp = await IpcClient.SendAsync(AppPaths.SocketPath, "add-account", [payload], TimeSpan.FromSeconds(30));
            if (resp != null)
            {
                (resp.Ok ? Console.Out : Console.Error).WriteLine(resp.Text);
                return resp.Ok ? 0 : 1;
            }

            var cfg = AppConfig.Load();
            if (cfg.ReadOnly)
            {
                Console.Error.WriteLine("A conta entrou, mas não foi salva: " + cfg.LoadWarning);
                return 1;
            }
            cfg.UpsertAccount(acc, drive.Id, drive.WebUrl);
            cfg.Save();
            Console.WriteLine($"Conta conectada: {acc.Username} ({(acc.IsPersonal ? "pessoal" : "corporativa/escolar")})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Falha no login: " + LoginErrors.ForConsole(ex));
            return 1;
        }
    }

    static int RunHeadless(AppHost host, IpcServer? early)
    {
        using var stop = new ManualResetEventSlim();
        host.QuitRequested += () => stop.Set();
        // SIGTERM (systemctl --user stop, logout), SIGINT (Ctrl+C), SIGHUP: mesmo caminho do "craftdriver quit".
        // Antes: ProcessExit/CancelKeyPress só sinalizavam e o runtime saía sem o ShutdownAsync (montagem caía
        // só pelo auto_unmount, configuração e socket ficavam).
        OnShutdownSignal = stop.Set;
        host.Start(early);
        Console.WriteLine($"craftDriver em execução (sem interface). Montado em {host.Config.MountPoint}. Ctrl+C para sair.");
        stop.Wait();
        host.ShutdownAsync().GetAwaiter().GetResult();
        return 0;
    }
}
