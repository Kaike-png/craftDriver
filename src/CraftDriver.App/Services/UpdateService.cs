using System.Diagnostics;
using CraftDriver.Core.Config;
using CraftDriver.Core.Sync;
using ActivityKind = CraftDriver.Core.Sync.ActivityKind;
using CraftDriver.Core.Updates;

namespace CraftDriver.App.Services;

public enum UpdatePhase
{
    /// <summary>Nada em andamento (pode haver versão nova disponível).</summary>
    Idle,
    Downloading,
    Installing,
    /// <summary>Pacote instalado; falta reiniciar.</summary>
    Installed,
    /// <summary>install-user.sh rodando desacoplado: o app vai fechar e abrir de novo.</summary>
    Restarting,
    Failed,
}

public enum UpdateOutcomeKind
{
    NothingToDo,
    /// <summary>Outra atualização já em andamento.</summary>
    Busy,
    Installed,
    /// <summary>Senha negada/cancelada (pkexec 126/127) ou saída não confirmada: o app continua como estava.</summary>
    Cancelled,
    Failed,
    /// <summary>Sem pacote para esta instalação/arquitetura ou sem pkexec: o texto diz o que rodar.</summary>
    Manual,
    /// <summary>Instalador do usuário iniciado; ele encerra e reabre o app.</summary>
    Restarting,
}

public sealed record UpdateOutcome(UpdateOutcomeKind Kind, string Message);

/// <summary>
/// Atualizações: procura versão nova no GitHub (2 min após iniciar, depois a cada 24 h, ou sob demanda), baixa o
/// pacote certo para o jeito de instalação, confere o SHA256 e instala (pkexec para pacote do sistema; install-user.sh
/// desacoplado para a instalação do usuário). Desenho em docs/atualizacoes.md.
/// </summary>
public sealed class UpdateService : IDisposable
{
    readonly AppConfig _config;
    readonly ActivityLog _activity;
    readonly Func<bool> _saveConfig;

    public UpdateService(AppConfig config, ActivityLog activity, Func<bool> saveConfig)
    {
        _config = config;
        _activity = activity;
        _saveConfig = saveConfig;
    }

    // ---------- costuras (testes) ----------
    internal Func<HttpClient> HttpFactory { get; set; } = () => new HttpClient();
    internal ICommandRunner Runner { get; set; } = ProcessRunner.Instance;
    internal Func<string, string?> Env { get; set; } = Environment.GetEnvironmentVariable;
    internal string CurrentVersion { get; set; } = AppVersion.Current;
    internal string? ExePath { get; set; } = Environment.ProcessPath;
    internal string Home { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    internal UpdateArch Arch { get; set; } = UpdateAssets.CurrentArch;
    internal string UpdatesDir { get; set; } = AppPaths.UpdatesDir;
    internal TimeSpan FirstCheckDelay { get; set; } = TimeSpan.FromMinutes(2);
    internal TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(24);
    /// <summary>Inicia o install-user.sh desacoplado (tarball, log); devolve o processo para acompanhar a saída.</summary>
    internal Func<string, string, Process?> LaunchUserInstall { get; set; } = UserInstallUpdate.Launch;

    // ---------- estado ----------
    /// <summary>Versão nova a oferecer (mais nova que a atual e não ignorada); null = nada.</summary>
    public ReleaseInfo? Available { get; private set; }
    public InstallInfo? Install { get; private set; }
    public UpdatePhase Phase { get; private set; }
    /// <summary>Progresso, erro ou instrução do painel.</summary>
    public string? StatusText { get; private set; }
    /// <summary>0–100 durante o download.</summary>
    public double Progress { get; private set; }
    public DateTimeOffset? LastCheck { get; private set; }
    /// <summary>Resultado da última verificação ("Você está na versão mais recente", erro…).</summary>
    public string? LastCheckText { get; private set; }
    /// <summary>A última verificação falhou (rede, GitHub); <see cref="LastCheckText"/> diz o motivo.</summary>
    public bool LastCheckFailed { get; private set; }
    /// <summary>Download/instalação em andamento (só uma por vez).</summary>
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    /// <summary>Verificação automática ligada (configuração e CRAFTDRIVER_NO_UPDATE_CHECK).</summary>
    public bool AutomaticEnabled => _config.CheckForUpdates && Env(UpdateCheck.DisableVariable) != "1";

    /// <summary>Estado mudou (qualquer thread).</summary>
    public event Action? Changed;
    /// <summary>Versão nova encontrada pela primeira vez (notificação: uma por versão).</summary>
    public event Action<ReleaseInfo>? Discovered;

    int _busy;
    Timer? _timer;
    readonly SemaphoreSlim _checkGate = new(1, 1);
    volatile bool _disposed;

    /// <summary>Agenda: primeira verificação depois de <see cref="FirstCheckDelay"/>, depois a cada <see cref="CheckInterval"/>.</summary>
    public void Start()
    {
        if (_timer != null || _disposed) return;
        // o timer roda sempre; cada disparo confere a configuração (desligar/religar vale sem reiniciar)
        _timer = new Timer(_ => _ = AutomaticCheckAsync(), null, FirstCheckDelay, CheckInterval);
    }

    async Task AutomaticCheckAsync()
    {
        try { await CheckAsync(manual: false).ConfigureAwait(false); }
        catch (Exception ex) { _activity.Debug("Verificação de atualização: " + ex.Message); }
    }

    /// <param name="manual">Pedido do usuário (botão/CLI): verifica mesmo com a automática desligada e mostra erros.</param>
    public async Task<ReleaseInfo?> CheckAsync(bool manual, CancellationToken ct = default)
    {
        if (!manual && !AutomaticEnabled) return null;
        await _checkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ReleaseInfo? latest;
            try
            {
                using var http = HttpFactory();
                latest = await UpdateCheck.FetchLatestAsync(http, UpdateCheck.ApiBase(Env), CurrentVersion, ct).ConfigureAwait(false);
            }
            catch (UpdateException ex)
            {
                // automática: silenciosa (só o log de depuração); tenta de novo no próximo ciclo
                _activity.Debug("Verificação de atualização falhou: " + ex.Message);
                LastCheckFailed = true;
                LastCheckText = ex.Message;
                Changed?.Invoke();
                return null;
            }
            LastCheck = DateTimeOffset.Now;
            LastCheckFailed = false;
            Install ??= await InstallDetector.DetectAsync(ExePath, Home, Runner, ct).ConfigureAwait(false);
            var newer = latest != null && SemVer.IsNewer(latest.Tag, CurrentVersion);
            if (newer && manual && _config.SkippedUpdateVersion == latest!.Version)
            {
                // "Procurar agora" é pedido explícito: mostra até a versão ignorada (e esquece o "ignorar")
                _config.SkippedUpdateVersion = null;
                _saveConfig();
            }
            var skipped = _config.SkippedUpdateVersion is { } s && SemVer.Core(s) is { } sv && latest != null && SemVer.Core(latest.Tag) <= sv;
            var offer = newer && !skipped ? latest : null;
            // não troca a release no meio de uma atualização (o painel mostra o progresso daquela)
            if (!IsBusy && Phase is not (UpdatePhase.Installed or UpdatePhase.Restarting)) Available = offer;
            LastCheckText = offer != null
                ? $"Versão {offer.Version} disponível (verificado às {LastCheck:HH:mm})."
                : newer ? $"A versão {latest!.Version} foi ignorada (verificado às {LastCheck:HH:mm})."
                : $"Você está na versão mais recente ({CurrentVersion}; verificado às {LastCheck:HH:mm}).";
            if (offer != null)
            {
                _activity.Debug($"Atualização disponível: {offer.Version} ({Install.Label})");
                if (_config.NotifiedUpdateVersion != offer.Version)
                {
                    _config.NotifiedUpdateVersion = offer.Version;
                    _saveConfig();
                    Discovered?.Invoke(offer);
                }
            }
            Changed?.Invoke();
            return offer;
        }
        finally { _checkGate.Release(); }
    }

    /// <summary>"Ignorar esta versão".</summary>
    public void Skip()
    {
        if (Available == null || IsBusy) return;
        _config.SkippedUpdateVersion = Available.Version;
        _saveConfig();
        _activity.Add(ActivityKind.Info, $"Versão {Available.Version} ignorada: o aviso volta quando sair uma mais nova.");
        Available = null;
        SetState(UpdatePhase.Idle, null);
    }

    void SetState(UpdatePhase phase, string? text, double progress = 0)
    {
        Phase = phase;
        StatusText = text;
        Progress = progress;
        Changed?.Invoke();
    }

    /// <summary>
    /// "Atualizar agora": baixa, confere e instala. <paramref name="confirmQuit"/> é chamado antes de iniciar o
    /// instalador do usuário (ele encerra o app); pacotes do sistema instalam com o app aberto.
    /// </summary>
    public async Task<UpdateOutcome> InstallAsync(Func<Task<bool>> confirmQuit, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return new(UpdateOutcomeKind.Busy, "Já há uma atualização em andamento.");
        try
        {
            if (Available is not { } release) return new(UpdateOutcomeKind.NothingToDo, "Nenhuma versão nova para instalar.");
            var install = Install ??= await InstallDetector.DetectAsync(ExePath, Home, Runner, ct).ConfigureAwait(false);
            if (UpdateAssets.Choose(release, install.Kind, Arch) is not { } asset)
            {
                var why = install.Kind == InstallKind.Unknown
                    ? $"Esta cópia do CraftDriver não pode se atualizar automaticamente (instalação {install.Label})."
                    : $"A release {release.Version} não tem pacote para esta instalação ({install.Label}, {Arch.ToString().ToLowerInvariant()}).";
                return Finish(UpdateOutcomeKind.Manual, UpdatePhase.Idle, why + "\n" + UpdateAssets.ManualHint(install.Kind, release));
            }

            SetState(UpdatePhase.Downloading, $"Baixando {asset.Name}…");
            string file;
            try
            {
                using var http = HttpFactory();
                http.Timeout = Timeout.InfiniteTimeSpan; // pacote de ~20 MB em conexão lenta; cancelável pelo token
                var progress = new InlineProgress(p =>
                {
                    var pct = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                    SetState(UpdatePhase.Downloading, $"Baixando {asset.Name}: {CraftDriver.App.ViewModels.Fmt.Bytes(p.Done)} de {CraftDriver.App.ViewModels.Fmt.Bytes(p.Total)}", pct);
                });
                file = await UpdateDownloader.DownloadAsync(http, release, asset, UpdatesDir, progress, ct).ConfigureAwait(false);
            }
            catch (UpdateException ex)
            {
                _activity.Add(ActivityKind.Error, "Atualização: " + ex.Message);
                return Finish(UpdateOutcomeKind.Failed, UpdatePhase.Failed, ex.Message);
            }
            _activity.Add(ActivityKind.Info, $"Atualização {release.Version} baixada e conferida (SHA256): {file}");

            if (install.Kind == InstallKind.UserInstall) return await RunUserInstallAsync(release, file, confirmQuit).ConfigureAwait(false);
            return await RunPackageInstallAsync(release, install, file, ct).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    async Task<UpdateOutcome> RunPackageInstallAsync(ReleaseInfo release, InstallInfo install, string file, CancellationToken ct)
    {
        var cmd = UpdateInstaller.PackageCommand(install.Kind, file, Runner.Exists);
        if (cmd == null || !Runner.Exists("pkexec"))
        {
            var terminal = UpdateInstaller.PackageCommand(install.Kind, file, Runner.Exists, "sudo")?.Display
                           ?? UpdateAssets.ManualHint(install.Kind, release);
            return Finish(UpdateOutcomeKind.Manual, UpdatePhase.Idle,
                $"O pacote foi baixado e conferido, mas não há como pedir a senha de administrador aqui (pkexec ausente). Rode no terminal:\n{terminal}");
        }
        SetState(UpdatePhase.Installing, $"Instalando a versão {release.Version} (o sistema pede a senha de administrador)…", 100);
        _activity.Add(ActivityKind.Info, "Atualização: " + cmd.Display);
        CommandResult rc;
        try { rc = await Runner.RunAsync(cmd.File, cmd.Args, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Finish(UpdateOutcomeKind.Failed, UpdatePhase.Failed, "Não foi possível rodar o instalador: " + ex.Message);
        }
        if (rc.ExitCode == 0)
        {
            _activity.Add(ActivityKind.Info, $"CraftDriver {release.Version} instalado; reinicie para usar a nova versão.");
            return Finish(UpdateOutcomeKind.Installed, UpdatePhase.Installed,
                $"A versão {release.Version} foi instalada. Reinicie o CraftDriver para usar a nova versão.");
        }
        if (UpdateInstaller.IsAuthCancelled(rc.ExitCode))
            return Finish(UpdateOutcomeKind.Cancelled, UpdatePhase.Idle,
                "Atualização cancelada: a senha de administrador não foi informada. O CraftDriver continua na versão atual.");
        var detail = string.Join('\n', (rc.StdErr + "\n" + rc.StdOut).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(4));
        _activity.Add(ActivityKind.Error, $"Atualização falhou ({cmd.File} saiu com {rc.ExitCode}): {detail}");
        return Finish(UpdateOutcomeKind.Failed, UpdatePhase.Failed,
            $"A instalação falhou (código {rc.ExitCode}). {detail}\nPara instalar à mão: {UpdateInstaller.PackageCommand(install.Kind, file, Runner.Exists, "sudo")?.Display}");
    }

    async Task<UpdateOutcome> RunUserInstallAsync(ReleaseInfo release, string tarball, Func<Task<bool>> confirmQuit)
    {
        // o install-user.sh encerra este app: a mesma confirmação do Sair (transferências em andamento)
        if (!await confirmQuit().ConfigureAwait(false))
            return Finish(UpdateOutcomeKind.Cancelled, UpdatePhase.Idle, "Atualização cancelada. O pacote já baixado fica guardado para a próxima vez.");
        var log = Path.Combine(Path.GetDirectoryName(tarball)!, "install.log");
        Process? p;
        try { p = LaunchUserInstall(tarball, log); }
        catch (Exception ex)
        {
            return Finish(UpdateOutcomeKind.Failed, UpdatePhase.Failed, "Não foi possível iniciar o instalador: " + ex.Message);
        }
        if (p != null)
        {
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                int code;
                try { code = p.ExitCode; }
                catch { return; }
                p.Dispose();
                // terminou e este processo continua vivo: o instalador falhou antes de encerrar o app
                if (code != 0 && !_disposed)
                {
                    _activity.Add(ActivityKind.Error, $"A atualização para {release.Version} falhou (código {code}); detalhes em {log}");
                    SetState(UpdatePhase.Failed, $"A instalação falhou (código {code}). Detalhes em {log}");
                }
            };
        }
        _activity.Add(ActivityKind.Info, $"Instalando a versão {release.Version}: o CraftDriver vai fechar e abrir de novo.");
        return Finish(UpdateOutcomeKind.Restarting, UpdatePhase.Restarting,
            $"Instalando a versão {release.Version}: o CraftDriver vai fechar e abrir de novo em instantes.");
    }

    UpdateOutcome Finish(UpdateOutcomeKind kind, UpdatePhase phase, string text)
    {
        SetState(phase, text, phase == UpdatePhase.Installed ? 100 : 0);
        return new UpdateOutcome(kind, text);
    }

    /// <summary>IProgress sem SynchronizationContext (o Progress&lt;T&gt; postaria na UI, fora de ordem).</summary>
    sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
