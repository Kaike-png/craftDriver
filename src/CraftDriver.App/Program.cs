using Avalonia;
using CraftDriver.App.Services;
using CraftDriver.Core.Auth;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.Ipc;

namespace CraftDriver.App;

sealed class Program
{
    static readonly string[] CliCommands = ["pin", "unpin", "free", "status", "sync", "pause", "resume", "quit", "deletes", "retry", "open", "exclude", "include"];

    [STAThread]
    public static int Main(string[] args)
    {
        try { AppPaths.MigrateLegacy(); }
        catch (Exception ex) { Console.Error.WriteLine("Aviso: migração da configuração antiga falhou: " + ex.Message); }
        AppPaths.EnsureDirs();

        if (args.Length > 0 && (CliCommands.Contains(args[0]) || ItemCommands.Contains(args[0])))
            return RunCli(args).GetAwaiter().GetResult();
        if (args.Contains("--version") || args.Contains("-V"))
        {
            Console.WriteLine("craftdriver " + (typeof(Program).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "?"));
            return 0;
        }
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }
        if (args.Length > 0 && args[0] == "login")
            return RunLogin(args).GetAwaiter().GetResult();

        // instância única: se já estiver rodando, só mostra a janela
        var running = IpcClient.SendAsync(AppPaths.SocketPath, "ping", [], TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        if (running is { Ok: true })
        {
            if (!args.Contains("--headless") && !args.Contains("--minimized"))
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
            Console.WriteLine("O craftDriver já está em execução.");
            return 0;
        }

        DesktopIntegration.RemoveLegacyLeftovers();
        // só a configuração (rápido); o AppHost — que espera o chaveiro por até 60 s — é criado depois
        var cfg = AppConfig.Load();
        if (cfg.LoadWarning is { } warning) Console.Error.WriteLine("Aviso: " + warning);

        if (args.Contains("--headless"))
        {
            // sem interface: espera o chaveiro aqui mesmo, como antes (nenhuma janela)
            App.Host = AppHost.CreateAsync(cfg, null, CancellationToken.None).GetAwaiter().GetResult();
            return RunHeadless(App.Host);
        }

        App.StartupConfig = cfg;
        App.StartMinimized = args.Contains("--minimized") || cfg.StartMinimized;
        // antes do AppBuilder: o backend X11 do Avalonia lê as variáveis de escala ao iniciar
        var scaling = DisplayScaling.Apply();
        App.ScalingNote = scaling.Note;
        // a interface sobe já; o AppHost é criado em segundo plano (App.StartAsync, com janela de espera)
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        return 0;
    }

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
              craftdriver login [--personal|--work]   adiciona conta pelo terminal

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
              craftdriver sync | pause | resume | quit
            """);
    }

    /// <summary>Link de compartilhamento, versões anteriores e Lixeira (também via IPC).</summary>
    static readonly string[] ItemCommands = ["share", "versions", "trash"];

    static async Task<int> RunCli(string[] args)
    {
        var notify = args.Contains("--notify");
        // "open" vem do menu do atalho (sem terminal): avisa por notificação quando falha, nunca quando dá certo
        var notifyErrors = notify || args[0] is "open" or "versions";
        // share --notify: a notificação (com "Abrir no navegador") sai da instância em execução, não daqui
        var hostNotifies = notify && args[0] == "share";
        var rest = args.Skip(1).Where(a => a != "--notify").Select(Path.GetFullPath).ToArray();
        if (args[0] is "sync" or "pause" or "resume" or "quit" or "retry" or "open") rest = [];
        if (args[0] is "deletes" or "trash") rest = args.Skip(1).Where(a => a != "--notify").ToArray();
        if (args[0] is "share" or "versions") rest = args.Skip(1).Select(a => a.StartsWith("--") ? a : Path.GetFullPath(a)).ToArray();
        var resp = await IpcClient.SendAsync(AppPaths.SocketPath, args[0], rest, TimeSpan.FromMinutes(2));
        if (resp == null)
        {
            const string msg = "O craftDriver não está em execução. Abra o aplicativo primeiro.";
            if (notifyErrors) await NotifyAndWait(msg);
            Console.Error.WriteLine(msg);
            return 2;
        }
        if (!hostNotifies && (notify || (notifyErrors && !resp.Ok))) await NotifyAndWait(resp.Text);
        Console.WriteLine(resp.Text);
        return resp.Ok ? 0 : 1;
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

    static int RunHeadless(AppHost host)
    {
        using var stop = new ManualResetEventSlim();
        host.QuitRequested += () => stop.Set();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Set();
        host.Start();
        Console.WriteLine($"craftDriver em execução (sem interface). Montado em {host.Config.MountPoint}. Ctrl+C para sair.");
        stop.Wait();
        host.ShutdownAsync().GetAwaiter().GetResult();
        return 0;
    }
}
