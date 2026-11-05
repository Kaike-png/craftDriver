using System.Text.Json;
using System.Text.Json.Serialization;

namespace CraftDriver.Core.Config;

/// <summary>Caminhos XDG usados pelo app.</summary>
public static class AppPaths
{
    public const string AppName = "craftdriver";

    static string Xdg(string var, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(var);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Permite isolar tudo num diretório (testes / perfis). Variável CRAFTDRIVER_HOME.</summary>
    static string? Override => Environment.GetEnvironmentVariable("CRAFTDRIVER_HOME");

    /// <summary>CRAFTDRIVER_HOME em uso (instância isolada: testes, desenvolvimento, verificação com conta real).</summary>
    public static string? HomeOverride => Override;

    public static string ConfigDir => Override is { } o ? Path.Combine(o, "config")
        : Path.Combine(Xdg("XDG_CONFIG_HOME", Path.Combine(Home, ".config")), AppName);

    public static string StateDir => Override is { } o ? Path.Combine(o, "state")
        : Path.Combine(Xdg("XDG_STATE_HOME", Path.Combine(Home, ".local", "state")), AppName);

    public static string DefaultCacheDir => Override is { } o ? Path.Combine(o, "cache")
        : Path.Combine(Xdg("XDG_CACHE_HOME", Path.Combine(Home, ".cache")), AppName);

    public static string RuntimeDir => Override is { } o ? Path.Combine(o, "run")
        : Xdg("XDG_RUNTIME_DIR", Path.Combine(Path.GetTempPath(), AppName + "-" + Environment.UserName));

    public static string SocketPath => Path.Combine(RuntimeDir, AppName + ".sock");
    public static string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public static string DatabaseFile => Path.Combine(StateDir, "state.db");
    public static string LogFile => Path.Combine(StateDir, "craftdriver.log");
    public static string DefaultMountPoint => Path.Combine(Home, "OneDrive");

    /// <summary>Nome usado até a versão 0.1 (antes de virar craftDriver).</summary>
    public const string LegacyAppName = "onedrive-linux";

    /// <summary>
    /// Migra configuração, estado e cache do nome antigo (onedrive-linux) para o novo.
    /// Só move se o destino ainda não existe. Retorna as pastas migradas.
    /// </summary>
    public static List<string> MigrateLegacy()
    {
        var moved = new List<string>();
        if (Override != null) return moved;
        foreach (var (envVar, fallback) in new[]
                 {
                     ("XDG_CONFIG_HOME", Path.Combine(Home, ".config")),
                     ("XDG_STATE_HOME", Path.Combine(Home, ".local", "state")),
                     ("XDG_CACHE_HOME", Path.Combine(Home, ".cache")),
                 })
        {
            var root = Xdg(envVar, fallback);
            var oldDir = Path.Combine(root, LegacyAppName);
            var newDir = Path.Combine(root, AppName);
            if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) continue;
            Directory.Move(oldDir, newDir);
            moved.Add(newDir);
        }
        // arquivos com o nome antigo dentro das pastas migradas
        var oldLog = Path.Combine(StateDir, LegacyAppName + ".log");
        if (File.Exists(oldLog) && !File.Exists(LogFile)) File.Move(oldLog, LogFile);
        // a config guarda o caminho do cache: atualiza se apontava para o padrão antigo
        if (File.Exists(ConfigFile))
        {
            var oldCache = Path.Combine(Xdg("XDG_CACHE_HOME", Path.Combine(Home, ".cache")), LegacyAppName);
            var text = File.ReadAllText(ConfigFile);
            var fixedText = text.Replace(JsonEncodedText.Encode(oldCache).ToString(), JsonEncodedText.Encode(DefaultCacheDir).ToString())
                                .Replace(oldCache, DefaultCacheDir);
            if (fixedText != text) File.WriteAllText(ConfigFile, fixedText);
        }
        return moved;
    }

    public static void EnsureDirs()
    {
        foreach (var d in new[] { ConfigDir, StateDir, RuntimeDir })
            Directory.CreateDirectory(d);
    }
}

public sealed class AccountConfig
{
    /// <summary>MSAL HomeAccountId.Identifier.</summary>
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string TenantId { get; set; } = "";
    /// <summary>"personal" ou "business".</summary>
    public string Type { get; set; } = "personal";

    [JsonIgnore] public bool IsPersonal => Type == "personal";
}

public sealed class DriveConfig
{
    public string DriveId { get; set; } = "";
    public string AccountId { get; set; } = "";
    /// <summary>Nome da pasta dentro do ponto de montagem.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>personal | business | documentLibrary</summary>
    public string Kind { get; set; } = "personal";
    public string? SiteName { get; set; }
    public string? WebUrl { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class AppConfig
{
    /// <summary>
    /// Client ID do registro do craftDriver no Microsoft Entra ID (criado quando o projeto se chamava
    /// "OneDrive Linux" — o nome exibido na tela de consentimento vem do registro, não daqui)
    /// (contas pessoais e organizacionais; redirects http://localhost e nativeclient).
    /// Troque pelo seu próprio registro no Entra ID quando possível.
    /// </summary>
    public const string DefaultClientId = "979715f8-d815-4f25-ac64-05cbccc74cc2";

    /// <summary>Versão do formato do config.json gravada por este craftDriver.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Versão do formato (arquivos sem o campo são da versão 1). Primeiro campo do JSON.</summary>
    [JsonPropertyOrder(-1)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// O arquivo é de um craftDriver mais novo: gravar por cima apagaria campos que esta versão não conhece,
    /// então <see cref="Save"/> não faz nada nesta execução.
    /// </summary>
    [JsonIgnore] public bool ReadOnly { get; private set; }

    public string ClientId { get; set; } = DefaultClientId;
    public string Authority { get; set; } = "https://login.microsoftonline.com/common";
    public string RedirectUri { get; set; } = "https://login.microsoftonline.com/common/oauth2/nativeclient";

    public string MountPoint { get; set; } = AppPaths.DefaultMountPoint;
    public string CacheDir { get; set; } = AppPaths.DefaultCacheDir;
    /// <summary>"system" (segue o portal/desktop), "light" ou "dark".</summary>
    public string Theme { get; set; } = "system";
    public int PollIntervalSeconds { get; set; } = 30;
    /// <summary>Downloads simultâneos por drive (sob demanda + fixados). 1–32; mudanças valem na hora (SyncEngine.ApplyTransferLimits).</summary>
    public int DownloadConcurrency { get; set; } = 4;
    /// <summary>Banda de download agregada por drive, em bytes/s. 0 (ou negativo) = ilimitado.</summary>
    public long DownloadBytesPerSecond { get; set; }
    /// <summary>Banda de upload por drive, em bytes/s. 0 (ou negativo) = ilimitado.</summary>
    public long UploadBytesPerSecond { get; set; }
    /// <summary>Limite do cache de arquivos sob demanda (não fixados). 0 = sem limite.</summary>
    public long CacheLimitMB { get; set; } = 10 * 1024;
    /// <summary>
    /// Leitura por faixas: arquivos só-na-nuvem a partir deste tamanho abrem na hora (para leitura) e baixam só os
    /// blocos lidos. 0 = desativado (sempre baixa o arquivo inteiro no open).
    /// </summary>
    public long StreamingThresholdMB { get; set; } = 8;
    /// <summary>Tamanho do bloco da leitura por faixas (cada bloco = um pedido Range). Limitado a 64 KiB–64 MiB.</summary>
    public int StreamingBlockKB { get; set; } = 4096;
    public bool StartMinimized { get; set; }
    /// <summary>
    /// Exclusão em massa: se mais itens que isso forem apagados de uma drive de uma vez, as exclusões
    /// ficam retidas até o usuário confirmar (como o aviso do cliente do Windows). 0 = desativado.
    /// </summary>
    public int MassDeleteThreshold { get; set; } = 50;
    public bool Paused { get; set; }

    /// <summary>
    /// Processos que NÃO podem baixar arquivos que só estão na nuvem
    /// (indexadores e geradores de miniatura baixariam o OneDrive inteiro).
    /// Comparado como substring do nome do programa (argv[0] sem o caminho, ou o nome da thread em /proc/PID/comm);
    /// os argumentos não contam (abrir "thumbnail.jpg" com o cat não é bloqueado).
    /// </summary>
    public List<string> BlockedProcesses { get; set; } =
    [
        "baloo", "tracker-extract", "tracker-miner", "localsearch", "tumblerd",
        "thumbnail", "ffmpegthumbnailer", "gnome-desktop-thumbnailer", "updatedb", "plocate", "mlocate"
    ];

    /// <summary>Arquivos que ficam só no Linux (nunca enviados). Glob simples com * e ?.</summary>
    public List<string> LocalOnlyPatterns { get; set; } =
    [
        ".~lock.*#", "~$*", ".goutputstream-*", "*.swp", "*.swx", "*.tmp", "*~.tmp",
        ".fuse_hidden*", ".directory", ".Trash-*", "*.kate-swp", ".nfs*"
    ];

    public List<AccountConfig> Accounts { get; set; } = [];
    public List<DriveConfig> Drives { get; set; } = [];

    /// <summary>
    /// Problema encontrado ao carregar (arquivo inválido → padrões; nomes de pasta ajustados). Mostrado na
    /// interface/terminal; o arquivo original inválido fica salvo ao lado.
    /// </summary>
    [JsonIgnore] public string? LoadWarning { get; private set; }

    /// <summary>
    /// Nomes de pasta das drives viram nomes de diretório no ponto de montagem: sem "/", não vazios,
    /// diferentes de "."/"..", e únicos (sem diferenciar maiúsculas, como o UniqueDisplayName) — duas drives
    /// com o mesmo nome deixavam a segunda inacessível. Retorna as renomeações feitas.
    /// </summary>
    public List<string> NormalizeDriveNames()
    {
        var changes = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Drives)
        {
            var name = d.DisplayName.Replace('/', '-').Trim();
            if (name is "" or "." or "..") name = "Drive";
            var unique = name;
            for (var i = 2; used.Contains(unique); i++) unique = $"{name} ({i})";
            used.Add(unique);
            if (unique != d.DisplayName)
            {
                changes.Add($"\"{d.DisplayName}\" → \"{unique}\"");
                d.DisplayName = unique;
            }
        }
        return changes;
    }

    /// <summary>
    /// Inclui (ou substitui) a conta e cria a drive principal dela se ainda não existir.
    /// Usado tanto pela instância aberta quanto pelo <c>craftdriver login</c> sem instância.
    /// Retorna a drive criada, ou null se ela já existia.
    /// </summary>
    public DriveConfig? UpsertAccount(AccountConfig acc, string driveId, string? driveWebUrl)
    {
        Accounts.RemoveAll(a => a.Id == acc.Id);
        Accounts.Add(acc);
        if (Drives.Any(d => d.DriveId == driveId)) return null;
        var label = acc.IsPersonal ? "OneDrive - Pessoal" : "OneDrive - " + Domain(acc.Username);
        var dc = new DriveConfig
        {
            DriveId = driveId, AccountId = acc.Id, DisplayName = UniqueDisplayName(label),
            Kind = acc.IsPersonal ? "personal" : "business", WebUrl = driveWebUrl,
        };
        Drives.Add(dc);
        return dc;
    }

    public string UniqueDisplayName(string wanted)
    {
        wanted = wanted.Replace('/', '-').Trim();
        var name = wanted;
        for (var i = 2; Drives.Any(d => d.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{wanted} ({i})";
        return name;
    }

    static string Domain(string username)
    {
        var at = username.IndexOf('@');
        if (at < 0) return username;
        var domain = username[(at + 1)..];
        var dot = domain.IndexOf('.');
        return dot > 0 ? domain[..dot] : domain;
    }

    static readonly object SaveLock = new();

    public static AppConfig Load(string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        if (!File.Exists(path)) return new AppConfig();
        AppConfig cfg;
        try
        {
            cfg = JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJsonContext.Default.AppConfig) ?? new AppConfig();
        }
        catch (JsonException ex)
        {
            // não sobrescreve cópias de falhas anteriores; a próxima gravação substitui o arquivo inválido
            var backup = $"{path}.corrompido-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, true);
            return new AppConfig
            {
                LoadWarning = $"A configuração ({path}) é inválida e foi ignorada: {ex.Message} " +
                              $"Contas e pastas não foram carregadas; o arquivo original foi guardado em {backup}. " +
                              "Corrija-o (ou copie-o de volta) com o craftDriver fechado.",
            };
        }
        if (cfg.SchemaVersion > CurrentSchemaVersion)
        {
            cfg.ReadOnly = true;
            cfg.LoadWarning = $"A configuração ({path}) é de uma versão mais nova do craftDriver (formato {cfg.SchemaVersion}; " +
                              $"esta entende até {CurrentSchemaVersion}). Ela foi carregada, mas alterações não serão gravadas " +
                              "nesta execução para não apagar o que esta versão não conhece. Atualize o craftDriver.";
            return cfg;
        }
        var renamed = cfg.NormalizeDriveNames();
        if (renamed.Count > 0)
            cfg.LoadWarning = "Nomes de pasta repetidos ou inválidos foram ajustados: " + string.Join(", ", renamed);
        return cfg;
    }

    /// <summary>
    /// Grava atomicamente (temporário + rename) com permissão 0600: o arquivo tem os ids das contas e o
    /// Client ID, e o temporário já nasce 0600, sem janela legível por outros usuários.
    /// </summary>
    public void Save(string? path = null)
    {
        if (ReadOnly) return;
        path ??= AppPaths.ConfigFile;
        lock (SaveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SchemaVersion = CurrentSchemaVersion;
            var tmp = path + ".tmp";
            File.Delete(tmp); // UnixCreateMode só vale para arquivo novo
            using (var fs = new FileStream(tmp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            using (var w = new StreamWriter(fs))
                w.Write(JsonSerializer.Serialize(this, ConfigJsonContext.Default.AppConfig));
            File.Move(tmp, path, true);
        }
    }

    // ---- janela principal e bandeja (acrescentados no fim: menos conflito com outras mudanças) ----

    /// <summary>Tamanho da janela principal (DIPs) no estado normal; null = padrão do XAML.</summary>
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    /// <summary>
    /// Posição (pixels de tela) da janela principal no estado normal; null = o sistema escolhe. Só é usada se
    /// couber numa tela atual. No Wayland/XWayland o compositor pode ignorar o pedido de posição.
    /// </summary>
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }
    /// <summary>O aviso "o craftDriver continua na bandeja" já foi mostrado (só aparece uma vez).</summary>
    public bool TrayNoticeShown { get; set; }
}
