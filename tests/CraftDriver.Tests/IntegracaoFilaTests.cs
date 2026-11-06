namespace CraftDriver.Tests;

/// <summary>
/// Fila paralela × exclusões que aguardam o período de silêncio: quem ocupa o nome que uma exclusão anterior vai liberar
/// espera por ela (Move/mkdir/envio de item novo vão com conflictBehavior=fail).
/// </summary>
public class IntegracaoFilaTests
{
    [Fact]
    public void Apagar_e_criar_de_novo_com_o_mesmo_nome_substitui_sem_copia_de_conflito()
    {
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "relatorio.txt", "antigo");
        h.Settle();
        Assert.Equal(0, h.Fs.Unlink(h.P("/relatorio.txt")));
        Assert.Equal(0, h.Fs.Create(h.P("/relatorio.txt"), 0x241 /* O_WRONLY|O_CREAT|O_TRUNC */, out var fh));
        var data = System.Text.Encoding.UTF8.GetBytes("novo");
        Assert.Equal(data.Length, h.Fs.Write(fh, data, 0));
        Assert.Equal(0, h.Fs.Release(fh));
        h.Settle();
        Assert.Equal("novo", h.Remote.ReadText("/relatorio.txt"));
        Assert.DoesNotContain(h.Remote.ListPaths(), p => p.Contains("conflito"));
        Assert.Empty(h.Db.Conflicts());
    }
}
