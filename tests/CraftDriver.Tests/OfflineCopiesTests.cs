using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;
using Xunit;

namespace CraftDriver.Tests;

/// <summary>
/// Fixados disponíveis com o app encerrado: cópias na pasta real do ponto de montagem ao encerrar e importação do que
/// mudou na abertura seguinte (docs/copias-offline.md). Sem FUSE: a "montagem" aqui é só uma pasta.
/// </summary>
public class OfflineCopiesTests
{
    sealed class Cenario : IDisposable
    {
        public readonly string Mount = Path.Combine(Path.GetTempPath(), "craftdriver-offline-" + Guid.NewGuid().ToString("N")[..8], "OneDrive");
        public Harness H;
        public Core.Sync.OfflineImportResult? Importado;

        public Cenario()
        {
            H = new Harness();
            H.Remote.RemoteCreate("/", "Fixada", null, folder: true);
            H.Remote.RemoteCreate("/Fixada", "a.txt", "versão A");
            H.Remote.RemoteCreate("/Fixada", "b.txt", "versão B");
            H.Remote.RemoteCreate("/Fixada", "Sub", null, folder: true);
            H.Remote.RemoteCreate("/Fixada/Sub", "c.txt", "versão C");
            H.Remote.RemoteCreate("/", "solto.txt", "não fixado");
            H.Settle();
            H.Engine.SetPinned(H.Fs.Resolve(H.P("/Fixada"))!.Value.Item!.Lid, true);
            Harness.WaitUntil(() => H.Db.PinnedNeedingDownload(FakeGraph.DriveId).Count == 0, "fixados baixados");
        }

        public string Real(string rel) => Path.Join(Mount, Harness.Name, rel.TrimStart('/'));

        /// <summary>"Encerrar": grava as cópias (o AppHost faz depois de desmontar) e para o motor.</summary>
        public OfflineMaterializeResult Encerrar()
        {
            var r = OfflineCopies.Materialize(H.Engine, H.Fs, Mount);
            H.Close();
            return r;
        }

        /// <summary>"Abrir": importa antes do motor começar (como o AppHost.Start) e espera a fila.</summary>
        public void Abrir()
        {
            var remote = H.Remote;
            H = new Harness(null, H.Dir, remote, e => Importado = OfflineCopies.Import(e, _ => false));
            H.Settle();
        }

        public void Dispose()
        {
            H.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(Mount)!, true); } catch { }
        }
    }

    [Fact]
    public void Encerrar_deixa_copias_dos_fixados_na_pasta_real()
    {
        using var c = new Cenario();
        var r = c.Encerrar();
        Assert.Equal(3, r.Files);
        Assert.Equal("versão A", File.ReadAllText(c.Real("/Fixada/a.txt")));
        Assert.Equal("versão C", File.ReadAllText(c.Real("/Fixada/Sub/c.txt")));
        Assert.False(File.Exists(c.Real("/solto.txt"))); // só os fixados (o resto é somente online)
    }

    [Fact]
    public void Encerrar_nao_sobrescreve_o_que_ja_existe_na_pasta_real()
    {
        using var c = new Cenario();
        Directory.CreateDirectory(Path.GetDirectoryName(c.Real("/Fixada/a.txt"))!);
        File.WriteAllText(c.Real("/Fixada/a.txt"), "arquivo do usuário escondido sob a montagem");
        var r = c.Encerrar();
        Assert.Equal(2, r.Files);
        Assert.Equal("arquivo do usuário escondido sob a montagem", File.ReadAllText(c.Real("/Fixada/a.txt")));
    }

    [Fact]
    public void Abrir_sem_mudancas_nao_envia_nada_e_limpa_a_pasta_real()
    {
        using var c = new Cenario();
        c.Encerrar();
        var uploads = c.H.Remote.Uploads;
        c.Abrir();
        Assert.NotNull(c.Importado);
        Assert.False(c.Importado!.AnyChange);
        Assert.Equal(uploads, c.H.Remote.Uploads);
        Assert.Empty(Directory.EnumerateFileSystemEntries(c.Mount)); // a montagem volta a mostrar o conteúdo
        Assert.Empty(c.H.Db.OfflineCopies());
    }

    [Fact]
    public void Edicao_e_arquivo_novo_com_o_app_fechado_sao_enviados()
    {
        using var c = new Cenario();
        c.Encerrar();
        File.WriteAllText(c.Real("/Fixada/a.txt"), "editado offline");
        Directory.CreateDirectory(c.Real("/Fixada/Nova"));
        File.WriteAllText(c.Real("/Fixada/Nova/n.txt"), "novo offline");
        File.WriteAllText(c.Real("/Fixada/Sub/d.txt"), "outro novo");
        c.Abrir();
        Assert.Equal(1, c.Importado!.Edited);
        Assert.Equal(3, c.Importado.Created); // pasta + 2 arquivos
        Assert.Equal("editado offline", c.H.Remote.ReadText("/Fixada/a.txt"));
        Assert.Equal("novo offline", c.H.Remote.ReadText("/Fixada/Nova/n.txt"));
        Assert.Equal("outro novo", c.H.Remote.ReadText("/Fixada/Sub/d.txt"));
        Assert.Equal("editado offline", c.H.ReadFile("/Fixada/a.txt"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(c.Mount));
    }

    [Fact]
    public void Editado_nos_dois_lados_gera_copia_de_conflito()
    {
        using var c = new Cenario();
        c.Encerrar();
        c.H.Remote.RemoteUpdate("/Fixada/b.txt", "mudou na nuvem");
        File.WriteAllText(c.Real("/Fixada/b.txt"), "mudou aqui");
        c.Abrir();
        Assert.Equal("mudou na nuvem", c.H.Remote.ReadText("/Fixada/b.txt"));
        var copia = c.H.Remote.ListPaths().Single(p => p.StartsWith("/Fixada/b (conflito", StringComparison.Ordinal));
        Assert.Equal("mudou aqui", c.H.Remote.ReadText(copia));
    }

    [Fact]
    public void Apagado_com_o_app_fechado_nao_apaga_na_nuvem()
    {
        using var c = new Cenario();
        c.Encerrar();
        File.Delete(c.Real("/Fixada/b.txt"));
        c.Abrir();
        Assert.Equal("versão B", c.H.Remote.ReadText("/Fixada/b.txt"));
        Assert.Equal("versão B", c.H.ReadFile("/Fixada/b.txt"));
    }

    [Fact]
    public void Pasta_fixada_apagada_inteira_fica_so_desfixada()
    {
        using var c = new Cenario();
        c.Encerrar();
        Directory.Delete(c.Real("/Fixada"), true);
        c.Abrir();
        Assert.Equal(1, c.Importado!.Unpinned);
        Assert.False(c.H.Fs.Resolve(c.H.P("/Fixada"))!.Value.Item!.Pinned);
        Assert.Equal("versão A", c.H.Remote.ReadText("/Fixada/a.txt"));
        Assert.Equal("versão C", c.H.Remote.ReadText("/Fixada/Sub/c.txt"));
    }

    [Fact]
    public void Renomeado_com_o_app_fechado_vira_mover_sem_duplicar()
    {
        using var c = new Cenario();
        c.Encerrar();
        File.Move(c.Real("/Fixada/a.txt"), c.Real("/Fixada/Sub/a-renomeado.txt"));
        c.Abrir();
        Assert.Equal(1, c.Importado!.Moved);
        Assert.Null(c.H.Remote.ReadText("/Fixada/a.txt"));
        Assert.Equal("versão A", c.H.Remote.ReadText("/Fixada/Sub/a-renomeado.txt"));
        Assert.Equal(0, c.Importado.Created);
    }

    [Fact]
    public void Arquivo_fora_de_pasta_conhecida_vai_para_recuperados()
    {
        using var c = new Cenario();
        c.Encerrar();
        File.WriteAllText(Path.Combine(c.Mount, "na-raiz.txt"), "sem drive");
        c.Abrir();
        Assert.Equal(1, c.Importado!.Recovered);
        Assert.NotNull(c.Importado.RecoveryDir);
        Assert.Equal("sem drive", File.ReadAllText(Path.Combine(c.Importado.RecoveryDir!, "na-raiz.txt")));
        Assert.False(File.Exists(Path.Combine(c.Mount, "na-raiz.txt")));
    }

    [Fact]
    public void Nao_importa_enquanto_a_pasta_estiver_montada()
    {
        using var c = new Cenario();
        c.Encerrar();
        File.WriteAllText(c.Real("/Fixada/a.txt"), "editado offline");
        var remote = c.H.Remote;
        c.H = new Harness(null, c.H.Dir, remote, e => c.Importado = OfflineCopies.Import(e, _ => true));
        c.H.Settle();
        Assert.Null(c.Importado);
        Assert.Equal("versão A", remote.ReadText("/Fixada/a.txt"));
        Assert.NotEmpty(c.H.Db.OfflineCopies()); // fica para a próxima abertura
    }

    [Fact]
    public void Importar_com_o_motor_ja_rodando_e_recusado()
    {
        // o delta já poderia ter adotado a versão da nuvem: o envio sobrescreveria sem cópia de conflito
        using var c = new Cenario();
        Assert.Throws<InvalidOperationException>(() => OfflineCopies.Import(c.H.Engine, _ => false));
    }

    [Fact]
    public void Copia_usa_reflink_quando_o_sistema_de_arquivos_permite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "craftdriver-reflink-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a"), "conteúdo");
            var antes = OfflineCopies.ReflinkCount;
            OfflineCopies.CloneOrCopy(Path.Combine(dir, "a"), Path.Combine(dir, "b"));
            Assert.Equal("conteúdo", File.ReadAllText(Path.Combine(dir, "b")));
            var fs = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("stat", ["-f", "-c", "%T", dir]) { RedirectStandardOutput = true })!;
            var tipo = fs.StandardOutput.ReadToEnd().Trim();
            fs.WaitForExit();
            if (tipo is "btrfs" or "xfs") Assert.True(OfflineCopies.ReflinkCount > antes, $"sem reflink em {tipo}");
        }
        finally { Directory.Delete(dir, true); }
    }

}

/// <summary>Ciclo completo com o FUSE de verdade: montar → encerrar → editar com "app fechado" → reabrir e montar de novo.</summary>
[Collection("fuse")]
public class OfflineCopiesFuseTests
{
    [Fact]
    public void Fixados_ficam_na_pasta_desmontada_e_a_edicao_aparece_ao_remontar()
    {
        RealFuse.Require();
        var h = new Harness();
        var mnt = Path.Combine(h.Dir, "mnt");
        try
        {
            h.Remote.RemoteCreate("/", "Fixada", null, folder: true);
            h.Remote.RemoteCreate("/Fixada", "a.txt", "versão A");
            h.Settle();
            h.Engine.SetPinned(h.Fs.Resolve(h.P("/Fixada"))!.Value.Item!.Lid, true);
            Harness.WaitUntil(() => h.Db.PinnedNeedingDownload(FakeGraph.DriveId).Count == 0, "fixados baixados");
            var real = Path.Combine(mnt, Harness.Name, "Fixada", "a.txt");

            void Montado(Action body)
            {
                var loop = CraftDriver.Fuse.FuseHost.MountAsync(h.Fs, mnt);
                try
                {
                    Harness.WaitUntil(() => OfflineCopies.IsMountPoint(mnt) && Directory.Exists(Path.Combine(mnt, Harness.Name)), "montagem visível");
                    body();
                }
                finally
                {
                    CraftDriver.Fuse.FuseHost.Unmount();
                    Assert.True(loop.Wait(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar");
                }
            }

            Montado(() => Assert.Equal("versão A", File.ReadAllText(real)));
            Assert.False(OfflineCopies.IsMountPoint(mnt));
            Assert.False(File.Exists(real)); // antes: desmontado = pasta vazia
            Assert.Equal(1, OfflineCopies.Materialize(h.Engine, h.Fs, mnt).Files);
            Assert.Equal("versão A", File.ReadAllText(real)); // app "fechado": o fixado está no disco
            h.Close();

            File.WriteAllText(real, "editado com o app fechado");
            OfflineImportResult? r = null;
            var remote = h.Remote;
            h = new Harness(null, h.Dir, remote, e => r = OfflineCopies.Import(e));
            Assert.Equal(1, r!.Edited);
            Assert.False(File.Exists(real)); // a cópia saiu da pasta real antes de montar
            Montado(() => Assert.Equal("editado com o app fechado", File.ReadAllText(real)));
            h.Settle();
            Assert.Equal("editado com o app fechado", remote.ReadText("/Fixada/a.txt"));
        }
        finally { h.Dispose(); }
    }
}
