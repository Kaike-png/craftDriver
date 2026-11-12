using System.Diagnostics;
using CraftDriver.Core.Config;
using CraftDriver.Core.Ipc;
using CraftDriver.Core.Updates;

namespace CraftDriver.App.Services;

/// <summary>
/// <c>craftdriver update --check</c> (só informa; funciona sem instância) e <c>craftdriver update</c> (com instância:
/// pede a ela o mesmo fluxo do botão "Atualizar agora"; sem instância: baixa, confere e instala aqui no terminal).
/// </summary>
public static class UpdateCli
{
    /// <summary>--check: está na versão mais recente.</summary>
    public const int UpToDate = 0;
    /// <summary>--check: há versão nova.</summary>
    public const int UpdateAvailable = 10;
    public const int Error = 1;

    /// <summary>Resposta do IPC "update" de uma instância sem janela: o CLI faz a atualização no terminal.</summary>
    public const string HeadlessReply = "sem-interface";

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var check = args.Contains("--check");
        if (args.Skip(1).Any(a => a != "--check"))
        {
            stderr.WriteLine("Uso: craftdriver update [--check]");
            return Error;
        }
        if (!check)
        {
            // app aberto: a janela dele faz a atualização (confirmações, pedido de senha gráfico, reinício)
            var resp = await IpcClient.SendAsync(AppPaths.SocketPath, "update", [], TimeSpan.FromMinutes(2));
            if (resp != null && resp.Text != HeadlessReply)
            {
                (resp.Ok ? stdout : stderr).WriteLine(resp.Text);
                return resp.Ok ? 0 : Error;
            }
            if (resp != null) stdout.WriteLine("O CraftDriver está rodando sem interface (--headless): a atualização é feita aqui.");
        }

        var current = AppVersion.Current;
        var runner = ProcessRunner.Instance;
        var install = await InstallDetector.DetectAsync(Environment.ProcessPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), runner);
        ReleaseInfo? latest;
        try
        {
            using var http = new HttpClient();
            latest = await UpdateCheck.FetchLatestAsync(http, UpdateCheck.ApiBase(Environment.GetEnvironmentVariable), current, CancellationToken.None);
        }
        catch (UpdateException ex)
        {
            stderr.WriteLine(ex.Message);
            return Error;
        }
        stdout.WriteLine("Versão instalada: " + current);
        stdout.WriteLine("Instalação: " + install.Label);
        if (latest == null || !SemVer.IsNewer(latest.Tag, current))
        {
            stdout.WriteLine("Você está na versão mais recente" + (latest != null ? $" (última publicada: {latest.Version})." : "."));
            return UpToDate;
        }
        stdout.WriteLine($"Versão nova: {latest.Version} — {latest.HtmlUrl}");
        var asset = UpdateAssets.Choose(latest, install.Kind, UpdateAssets.CurrentArch);
        if (check)
        {
            stdout.WriteLine(asset != null ? "Para atualizar: craftdriver update" : UpdateAssets.ManualHint(install.Kind, latest));
            return UpdateAvailable;
        }
        if (asset == null)
        {
            stdout.WriteLine("Esta cópia do CraftDriver não pode se atualizar automaticamente.");
            stdout.WriteLine(UpdateAssets.ManualHint(install.Kind, latest));
            return Error;
        }
        return await InstallHereAsync(latest, install, asset, runner, stdout, stderr);
    }

    /// <summary>Sem instância com janela: baixa, confere e roda o instalador neste terminal.</summary>
    static async Task<int> InstallHereAsync(ReleaseInfo release, InstallInfo install, ReleaseAsset asset, ProcessRunner runner,
        TextWriter stdout, TextWriter stderr)
    {
        string file;
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var last = -1;
            var progress = new ProgressSink(p =>
            {
                var pct = p.Total > 0 ? (int)(100 * p.Done / p.Total) : 0;
                if (pct / 10 == last / 10) return;
                last = pct;
                stderr.WriteLine($"Baixando {asset.Name}: {pct}%");
            });
            file = await UpdateDownloader.DownloadAsync(http, release, asset, AppPaths.UpdatesDir, progress, CancellationToken.None);
        }
        catch (UpdateException ex)
        {
            stderr.WriteLine(ex.Message);
            return Error;
        }
        stdout.WriteLine($"Baixado e conferido (SHA256): {file}");

        ProcessStartInfo psi;
        if (install.Kind == InstallKind.UserInstall)
        {
            var work = Path.Combine(Path.GetTempPath(), "craftdriver-atualizacao-" + Guid.NewGuid().ToString("N")[..8]);
            psi = UserInstallUpdate.Build(file, work, null, null, null, [], detach: false);
        }
        else
        {
            // terminal interativo: sudo (o gerenciador mostra as perguntas); sem terminal: pkexec (janela de senha)
            var elevate = !Console.IsInputRedirected && runner.Exists("sudo") ? "sudo" : "pkexec";
            if (UpdateInstaller.PackageCommand(install.Kind, file, runner.Exists, elevate) is not { } cmd || !runner.Exists(elevate))
            {
                stdout.WriteLine("Instale com o gerenciador de pacotes: " +
                                 (UpdateInstaller.PackageCommand(install.Kind, file, _ => true, "sudo")?.Display ?? file));
                return Error;
            }
            stdout.WriteLine("Rodando: " + cmd.Display);
            psi = new ProcessStartInfo(cmd.File) { UseShellExecute = false };
            foreach (var a in cmd.Args) psi.ArgumentList.Add(a);
        }
        int rc;
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            rc = p.ExitCode;
        }
        catch (Exception ex)
        {
            stderr.WriteLine("Não foi possível rodar o instalador: " + ex.Message);
            return Error;
        }
        if (rc == 0)
        {
            stdout.WriteLine($"CraftDriver {release.Version} instalado." + (install.Kind == InstallKind.UserInstall
                ? " Abra o CraftDriver de novo."
                : " Se ele estiver aberto, reinicie-o (craftdriver quit e abra de novo)."));
            return 0;
        }
        stderr.WriteLine(psi.FileName == "pkexec" && UpdateInstaller.IsAuthCancelled(rc)
            ? "Atualização cancelada: a senha de administrador não foi informada."
            : $"A instalação falhou (código {rc}).");
        return Error;
    }

    sealed class ProgressSink(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
