using Avalonia.Threading;
using CraftDriver.App.Views;

namespace CraftDriver.App.Services;

/// <summary>
/// Cria o AppHost (lento: espera o chaveiro) fora da thread de UI, com a janela de espera se demorar.
/// Genérico para os testes injetarem uma fábrica lenta sem montar um AppHost.
/// Resultado: o objeto criado, ou null quando o usuário saiu pela janela de espera (ou fechou após um erro).
/// </summary>
public sealed class StartupCoordinator<T> where T : class
{
    /// <summary>Abaixo disso a janela de espera nem aparece (evita piscar quando o chaveiro já está aberto).</summary>
    public static readonly TimeSpan DefaultSplashDelay = TimeSpan.FromMilliseconds(400);

    readonly Func<Action<string>, CancellationToken, Task<T>> _factory;
    readonly CancellationTokenSource _skip = new();
    readonly TaskCompletionSource<T?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    string _status = "Iniciando o craftDriver…";
    string? _error;
    bool _finished;

    /// <param name="factory">Recebe o relato de progresso e o token "continuar sem o chaveiro".</param>
    public StartupCoordinator(Func<Action<string>, CancellationToken, Task<T>> factory) => _factory = factory;

    /// <summary>Janela de espera aberta agora (null se não apareceu ou já fechou).</summary>
    public SplashWindow? Splash { get; private set; }

    /// <summary>Tarefa da fábrica (testes: conferir que terminou/foi abandonada).</summary>
    public Task<T>? FactoryTask { get; private set; }

    /// <summary>Usuário pediu para não esperar o chaveiro.</summary>
    public bool Skipped => _skip.IsCancellationRequested;

    /// <summary>Chamar na thread de UI.</summary>
    public Task<T?> RunAsync(bool showSplash, TimeSpan? splashDelay = null)
    {
        FactoryTask = Task.Run(() => _factory(s => Dispatcher.UIThread.Post(() => SetStatus(s)), _skip.Token));
        FactoryTask.ContinueWith(t => Dispatcher.UIThread.Post(() => OnFactoryDone(t)), TaskScheduler.Default);
        if (showSplash)
            Task.Delay(splashDelay ?? DefaultSplashDelay).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                if (!_finished) ShowSplash();
            }), TaskScheduler.Default);
        return _result.Task;
    }

    /// <summary>Mostra (ou traz para frente) a janela de espera — ex.: clique na bandeja durante o início.</summary>
    public void ShowSplash()
    {
        if (_finished && _error == null) return;
        if (Splash == null)
        {
            Splash = new SplashWindow { Status = _status };
            Splash.SkipRequested += Skip;
            Splash.QuitRequested += Quit;
            if (_error != null) Splash.ShowError(_error);
        }
        Splash.Show();
        Splash.Activate();
    }

    void SetStatus(string s)
    {
        _status = s;
        if (Splash != null && !_skip.IsCancellationRequested) Splash.Status = s;
    }

    public void Skip() => _skip.Cancel();

    /// <summary>Sair durante a inicialização: não espera a fábrica (ela termina sozinha e é descartada).</summary>
    public void Quit()
    {
        _finished = true;
        _skip.Cancel();
        var s = Splash;
        Splash = null;
        s?.CloseQuietly();
        _result.TrySetResult(null);
    }

    void OnFactoryDone(Task<T> t)
    {
        if (_result.Task.IsCompleted) return; // usuário já saiu
        if (t.IsCompletedSuccessfully)
        {
            _finished = true;
            var s = Splash;
            Splash = null;
            s?.CloseQuietly();
            _result.TrySetResult(t.Result);
            return;
        }
        // erro: fica na janela até o usuário fechar (sem ela, o app sumiria sem explicação)
        _error = t.Exception?.GetBaseException().Message ?? "A inicialização foi interrompida.";
        Console.Error.WriteLine("Falha ao iniciar: " + t.Exception?.GetBaseException());
        ShowSplash();
        Splash?.ShowError(_error);
    }
}
