using Avalonia;
using CraftDriver.App.Services;
using CraftDriver.Core.Config;
using CraftDriver.Core.Ipc;

namespace CraftDriver.App;

sealed class Program
{
    static readonly string[] CliCommands = ["pin", "unpin", "free", "status", "sync", "pause", "resume", "quit", "deletes"];

    [STAThread]
    public static int Main(string[] args)
    {
        try { AppPaths.MigrateLegacy(); }
        catch (Exception ex) { Console.Error.WriteLine("Aviso: migração da configuração antiga falhou: " + ex.Message); }
        AppPaths.EnsureDirs();

        if (args.Length > 0 && CliCommands.Contains(args[0]))
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
            if (!args.Contains("--headless"))
                IpcClient.SendAsync(AppPaths.SocketPath, "show", []).GetAwaiter().GetResult();
            Console.WriteLine("O craftDriver já está em execução.");
            return 0;
        }

        App.Host = AppHost.CreateAsync().GetAwaiter().GetResult();

        if (args.Contains("--headless"))
            return RunHeadless(App.Host);

        App.StartMinimized = args.Contains("--minimized") || App.Host.Config.StartMinimized;
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
              craftdriver deletes [confirm|restore]   exclusão em massa retida
              craftdriver sync | pause | resume | quit
            """);
    }

    static async Task<int> RunCli(string[] args)
    {
        var notify = args.Contains("--notify");
        var rest = args.Skip(1).Where(a => a != "--notify").Select(Path.GetFullPath).ToArray();
        if (args[0] is "sync" or "pause" or "resume" or "quit") rest = [];
        if (args[0] == "deletes") rest = args.Skip(1).Where(a => a != "--notify").ToArray();
        var resp = await IpcClient.SendAsync(AppPaths.SocketPath, args[0], rest, TimeSpan.FromMinutes(2));
        if (resp == null)
        {
            const string msg = "O craftDriver não está em execução. Abra o aplicativo primeiro.";
            if (notify) DesktopIntegration.Notify("craftDriver", msg);
            Console.Error.WriteLine(msg);
            return 2;
        }
        if (notify) DesktopIntegration.Notify("craftDriver", resp.Text);
        Console.WriteLine(resp.Text);
        return resp.Ok ? 0 : 1;
    }

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
        var host = await AppHost.CreateAsync();
        try
        {
            var acc = personal && !args.Contains("--manual")
                ? await host.AddAccountWithSystemBrowserAsync(true, uri =>
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
                ? await host.AddAccountWithBrowserAsync(true, (authUri, ct) =>
                {
                    Console.WriteLine();
                    Console.WriteLine("1. Abra esta página no navegador e entre com sua conta:");
                    Console.WriteLine(authUri.AbsoluteUri);
                    Console.WriteLine();
                    Console.WriteLine("2. No final o navegador mostra uma página em branco. Cole aqui o endereço inteiro dela:");
                    while (true)
                    {
                        Console.Write("> ");
                        var err = CraftDriver.Core.Auth.AuthService.ValidatePastedRedirect(Console.ReadLine(), out var uri);
                        if (uri != null) return Task.FromResult(uri);
                        Console.WriteLine(err);
                    }
                }, CancellationToken.None)
                : await host.AddAccountAsync(false, (url, code, message) =>
            {
                Console.WriteLine();
                Console.WriteLine($"Abra {url} e digite o código: {code}");
                Console.WriteLine();
                return Task.CompletedTask;
            }, CancellationToken.None);
            Console.WriteLine($"Conta conectada: {acc.Username} ({(acc.IsPersonal ? "pessoal" : "corporativa/escolar")})");
            Console.WriteLine("Se o app já estiver aberto, reinicie-o para carregar a nova conta.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Falha no login: " + ex.Message);
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
