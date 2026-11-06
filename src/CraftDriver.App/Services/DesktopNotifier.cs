using System.Collections.Concurrent;
using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace CraftDriver.App.Services;

/// <summary>
/// Botão de uma notificação. <see cref="OnInvoke"/> roda numa thread do pool (nunca na do motor nem na
/// de leitura do D-Bus), então pode demorar; para mexer na interface, despache para a thread de UI.
/// </summary>
public sealed record NotificationAction(string Id, string Label, Action OnInvoke)
{
    /// <summary>Id que o servidor de notificações usa para o clique no corpo da notificação (sem botão).</summary>
    public const string Default = "default";

    /// <summary>
    /// Como fazer o mesmo sem o botão — acrescentado ao texto quando o servidor não mostra ações (sem a capacidade
    /// <c>actions</c>, ex.: alguns daemons mínimos) ou no notify-send. Sem isto a notificação ficava sem botão e sem dica.
    /// </summary>
    public string? HintWithoutButtons { get; init; }
}

/// <summary>
/// Notificações da área de trabalho pelo D-Bus (org.freedesktop.Notifications), com ações e a dica
/// desktop-entry=craftdriver (o servidor agrupa e mostra o nome/ícone do app). Mantém uma conexão de
/// sessão aberta para receber ActionInvoked / NotificationClosed. Sem D-Bus (variável ausente, barramento
/// fora do ar ou chamada falhou) cai para o <c>notify-send</c> — sem botões, só título e texto.
/// </summary>
public sealed class DesktopNotifier : IDisposable
{
    const string Service = "org.freedesktop.Notifications";
    const string ObjPath = "/org/freedesktop/Notifications";
    const string Iface = "org.freedesktop.Notifications";
    static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    readonly string? _address;
    readonly string _notifySend;
    readonly string _appName;
    readonly string _desktopEntry;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly ConcurrentDictionary<uint, IReadOnlyList<NotificationAction>> _callbacks = new();
    /// <summary>Etiqueta → id da última notificação mostrada com ela (substituir/fechar).</summary>
    readonly ConcurrentDictionary<string, uint> _tags = new();
    DBusConnection? _conn;
    /// <summary>Capacidades do servidor nesta conexão (GetCapabilities); null = ainda não consultadas.</summary>
    Task<bool>? _supportsActions;
    readonly List<IDisposable> _watches = [];
    readonly Lock _capsLock = new();
    bool _disposed;

    /// <summary>Diagnóstico (falhas do D-Bus, uso do fallback). Padrão: nada.</summary>
    public Action<string>? Log { get; set; }

    /// <param name="busAddress">Endereço do barramento de sessão; null/vazio = sem D-Bus (só notify-send).</param>
    /// <param name="notifySend">Executável do fallback (nome no PATH ou caminho).</param>
    public DesktopNotifier(string? busAddress, string notifySend = "notify-send", string appName = "craftDriver", string desktopEntry = "craftdriver")
    {
        _address = string.IsNullOrWhiteSpace(busAddress) ? null : busAddress;
        _notifySend = notifySend;
        _appName = appName;
        _desktopEntry = desktopEntry;
    }

    /// <summary>
    /// Endereço do barramento de sessão do ambiente: DBUS_SESSION_BUS_ADDRESS ou, sem ela, o soquete padrão
    /// do systemd ($XDG_RUNTIME_DIR/bus). Null quando nenhum existe.
    /// </summary>
    public static string? SessionBusAddress()
    {
        if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is { Length: > 0 } a) return a;
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } rt && File.Exists(Path.Combine(rt, "bus")))
            return "unix:path=" + Path.Combine(rt, "bus");
        return null;
    }

    /// <summary>Notificações com ações ainda abertas (callbacks guardados até NotificationClosed).</summary>
    internal int PendingCallbacks => _callbacks.Count;

    /// <summary>
    /// Mostra a notificação. Devolve o id do servidor quando foi pelo D-Bus, ou null quando usou o
    /// notify-send (ou nada funcionou — aí escreve no console). Nunca lança.
    /// </summary>
    public async Task<uint?> NotifyAsync(string title, string body, string icon, IReadOnlyList<NotificationAction> actions, string? tag = null)
    {
        if (_address != null && !_disposed)
        {
            // uma nova tentativa com conexão nova: a antiga pode ter caído (reinício do barramento/servidor)
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var conn = await ConnectionAsync().ConfigureAwait(false);
                    var replaces = tag != null && _tags.TryGetValue(tag, out var old) ? old : 0u;
                    var (shownBody, shownActions) = actions.Count > 0 && !await SupportsActionsAsync(conn).ConfigureAwait(false)
                        ? (WithHints(body, actions), (IReadOnlyList<NotificationAction>)[])
                        : (body, actions);
                    var id = await SendAsync(conn, title, shownBody, icon, shownActions, replaces).WaitAsync(CallTimeout).ConfigureAwait(false);
                    if (tag != null)
                    {
                        // o servidor costuma devolver o mesmo id ao substituir; se não, os botões da antiga saem
                        if (replaces != 0 && replaces != id) _callbacks.TryRemove(replaces, out _);
                        _tags[tag] = id;
                    }
                    return id;
                }
                catch (DBusErrorReplyException ex)
                {
                    // a conexão está boa; o barramento respondeu erro (ex.: nenhum servidor de notificações)
                    Log?.Invoke($"Notificação pelo D-Bus recusada ({ex.ErrorName}); usando notify-send");
                    break;
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Notificação pelo D-Bus falhou ({ex.GetType().Name}: {ex.Message})");
                    await ResetAsync().ConfigureAwait(false);
                }
            }
        }
        RunNotifySend(title, WithHints(body, actions), icon); // notify-send não tem botões
        return null;
    }

    /// <summary>Texto com as dicas das ações que não vão aparecer como botão.</summary>
    internal static string WithHints(string body, IReadOnlyList<NotificationAction> actions)
    {
        var hints = actions.Select(a => a.HintWithoutButtons).OfType<string>().Distinct().ToList();
        return hints.Count == 0 ? body : body + "\n" + string.Join("\n", hints);
    }

    /// <summary>O servidor mostra botões (capacidade "actions")? Consultado uma vez por conexão; erro = não.</summary>
    Task<bool> SupportsActionsAsync(DBusConnection conn)
    {
        lock (_capsLock)
            return _supportsActions ??= QueryActionsAsync(conn);
    }

    async Task<bool> QueryActionsAsync(DBusConnection conn)
    {
        try
        {
            MessageBuffer msg;
            using (var w = conn.GetMessageWriter())
            {
                w.WriteMethodCallHeader(Service, ObjPath, Iface, "GetCapabilities", null, MessageFlags.None);
                msg = w.CreateMessage();
            }
            var caps = await conn.CallMethodAsync(msg, static (m, _) => m.GetBodyReader().ReadArrayOfString(), null)
                .WaitAsync(CallTimeout).ConfigureAwait(false);
            return caps.Contains("actions");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"GetCapabilities falhou ({ex.GetType().Name}: {ex.Message}); notificações sem botões");
            return false;
        }
    }

    async Task<DBusConnection> ConnectionAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_conn != null) return _conn;
            var conn = new DBusConnection(_address!);
            try
            {
                await conn.ConnectAsync().ConfigureAwait(false);
                // emitOnCapturedContext=false: o handler roda no laço de leitura; ele só despacha para o pool
                _watches.Add(await conn.WatchSignalAsync(null!, ObjPath, Iface, "ActionInvoked",
                    static (m, _) => { var r = m.GetBodyReader(); return (Id: r.ReadUInt32(), Key: r.ReadString()); },
                    n => { if (n.HasValue) OnActionInvoked(n.Value.Id, n.Value.Key); }, ObserverFlags.None, false, null).ConfigureAwait(false));
                _watches.Add(await conn.WatchSignalAsync(null!, ObjPath, Iface, "NotificationClosed",
                    static (m, _) => m.GetBodyReader().ReadUInt32(),
                    n => { if (n.HasValue) Forget(n.Value); }, ObserverFlags.None, false, null).ConfigureAwait(false));
            }
            catch
            {
                DisposeWatches();
                conn.Dispose();
                throw;
            }
            _conn = conn;
            return conn;
        }
        finally { _gate.Release(); }
    }

    Task<uint> SendAsync(DBusConnection conn, string title, string body, string icon, IReadOnlyList<NotificationAction> actions, uint replacesId)
    {
        MessageBuffer msg;
        using (var w = conn.GetMessageWriter())
        {
            w.WriteMethodCallHeader(Service, ObjPath, Iface, "Notify", "susssasa{sv}i", MessageFlags.None);
            w.WriteString(_appName);
            w.WriteUInt32(replacesId); // 0 = nova
            w.WriteString(icon);
            w.WriteString(title);
            w.WriteString(body);
            var arr = w.WriteArrayStart(DBusType.String);
            foreach (var a in actions)
            {
                w.WriteString(a.Id);
                w.WriteString(a.Label);
            }
            w.WriteArrayEnd(arr);
            var dict = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart();
            w.WriteString("desktop-entry");
            w.WriteVariantString(_desktopEntry);
            w.WriteDictionaryEnd(dict);
            w.WriteInt32(-1); // expire_timeout: padrão do servidor
            msg = w.CreateMessage();
        }
        // o leitor da resposta roda no laço de recepção, antes de qualquer sinal seguinte do servidor:
        // o callback já está registrado quando um ActionInvoked desse id chegar
        return conn.CallMethodAsync(msg, (m, _) =>
        {
            var id = m.GetBodyReader().ReadUInt32();
            if (actions.Count > 0) _callbacks[id] = actions;
            else _callbacks.TryRemove(id, out var _); // substituiu uma com botões por outra sem
            return id;
        }, null);
    }

    /// <summary>Fechada (pelo usuário, expirou ou por nós): solta os botões e a etiqueta.</summary>
    void Forget(uint id)
    {
        _callbacks.TryRemove(id, out _);
        foreach (var (tag, tid) in _tags)
            if (tid == id) _tags.TryRemove(new KeyValuePair<string, uint>(tag, tid));
    }

    /// <summary>Fecha a notificação mostrada com a etiqueta (CloseNotification) e esquece os botões dela. Nunca lança.</summary>
    public async Task CloseAsync(string tag)
    {
        if (!_tags.TryRemove(tag, out var id)) return;
        _callbacks.TryRemove(id, out _);
        if (_address == null || _disposed) return;
        // conexão caída desde o aviso: reconecta (como o NotifyAsync) — o servidor costuma ser o mesmo e o id ainda vale
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var conn = await ConnectionAsync().ConfigureAwait(false);
                MessageBuffer msg;
                using (var w = conn.GetMessageWriter())
                {
                    w.WriteMethodCallHeader(Service, ObjPath, Iface, "CloseNotification", "u", MessageFlags.None);
                    w.WriteUInt32(id);
                    msg = w.CreateMessage();
                }
                await conn.CallMethodAsync(msg).WaitAsync(CallTimeout).ConfigureAwait(false);
                return;
            }
            catch (DBusErrorReplyException ex)
            {
                Log?.Invoke($"Não foi possível fechar a notificação ({ex.ErrorName})");
                return;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Não foi possível fechar a notificação ({ex.GetType().Name}: {ex.Message})");
                await ResetAsync().ConfigureAwait(false);
            }
        }
    }

    void OnActionInvoked(uint id, string key)
    {
        if (!_callbacks.TryGetValue(id, out var actions)) return; // de outro app, ou já fechada
        var action = actions.FirstOrDefault(a => a.Id == key);
        if (action == null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { action.OnInvoke(); }
            catch (Exception ex) { Log?.Invoke($"Ação \"{action.Label}\" da notificação falhou: {ex.Message}"); }
        });
    }

    void RunNotifySend(string title, string body, string icon)
    {
        try
        {
            var psi = new ProcessStartInfo(_notifySend) { UseShellExecute = false };
            foreach (var a in new[] { "-a", _appName, "-i", icon, "-h", "string:desktop-entry:" + _desktopEntry, "--", title, body })
                psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            if (p != null)
            {
                p.EnableRaisingEvents = true; // recolhe o processo ao terminar, sem esperar por ele
                p.Exited += (_, _) => p.Dispose();
            }
        }
        catch { Console.WriteLine($"{title}: {body}"); }
    }

    async Task ResetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeWatches();
            _conn?.Dispose();
            _conn = null;
            lock (_capsLock) _supportsActions = null; // o servidor pode ser outro
            _callbacks.Clear();
            // as etiquetas ficam: quase sempre só a conexão caiu e o servidor (e os ids) é o mesmo — sem o id, o aviso
            // antigo ("sessão expirada") não era substituído nem fechado ao entrar de novo. Id de um servidor novo não
            // existe lá: replaces_id desconhecido vira notificação nova e CloseNotification é ignorado (especificação)
        }
        finally { _gate.Release(); }
    }

    void DisposeWatches()
    {
        foreach (var w in _watches)
            try { w.Dispose(); } catch { }
        _watches.Clear();
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            DisposeWatches();
            _conn?.Dispose();
            _conn = null;
            _callbacks.Clear();
        }
        finally { _gate.Release(); }
    }
}
