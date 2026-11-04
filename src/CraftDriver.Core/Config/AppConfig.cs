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
    /// Client ID do app "OneDrive Linux" registrado no Microsoft Entra ID
    /// (contas pessoais e organizacionais; redirects http://localhost e nativeclient).
    /// Troque pelo seu próprio registro no Entra ID quando possível.
    /// </summary>
    public const string DefaultClientId = "979715f8-d815-4f25-ac64-05cbccc74cc2";

    public string ClientId { get; set; } = DefaultClientId;
    public string Authority { get; set; } = "https://login.microsoftonline.com/common";
    public string RedirectUri { get; set; } = "https://login.microsoftonline.com/common/oauth2/nativeclient";

    public string MountPoint { get; set; } = AppPaths.DefaultMountPoint;
    public string CacheDir { get; set; } = AppPaths.DefaultCacheDir;
    public int PollIntervalSeconds { get; set; } = 30;
    /// <summary>Limite do cache de arquivos sob demanda (não fixados). 0 = sem limite.</summary>
    public long CacheLimitMB { get; set; } = 10 * 1024;
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
    /// Comparado como substring da linha de comando do processo.
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

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    static readonly object SaveLock = new();

    public static AppConfig Load(string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        if (!File.Exists(path)) return new AppConfig();
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOpts) ?? new AppConfig();
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".corrompido", true);
            return new AppConfig();
        }
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        lock (SaveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, path, true);
        }
    }
}
