using System.Diagnostics;
using CraftDriver.Core.Config;
using CraftDriver.Core.State;
using CraftDriver.Core.Sync;
using CraftDriver.Core.Vfs;
using CraftDriver.Fuse;

namespace CraftDriver.Tests;

/// <summary>
/// Duas drives na mesma montagem (motor + VFS + duas Graphs falsas). A configuração passa por
/// <see cref="AppConfig.Save"/>/<see cref="AppConfig.Load"/>, como na aplicação (é lá que nomes repetidos são ajustados).
/// </summary>
sealed class TwoDrives : IDisposable
{
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "craftdriver-test-" + Guid.NewGuid().ToString("N")[..8]);
    public const string Id2 = "fake-drive-2";
    public readonly FakeGraph R1 = new(), R2 = new(Id2);
    public readonly AppConfig Config;
    public readonly StateDb Db;
    public readonly SyncEngine Engine;
    public readonly VirtualFs Fs;

    /// <param name="kind2">A segunda é uma biblioteca do SharePoint por padrão (sem "Compartilhados comigo").</param>
    public TwoDrives(string name1, string name2, string kind1 = "personal", string kind2 = "documentLibrary")
    {
        DriveSync.UploadSettleMs = 1000;
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, "config.json");
        new AppConfig
        {
            CacheDir = Path.Combine(Dir, "cache"),
            PollIntervalSeconds = 5,
            Accounts = [new AccountConfig { Id = "acc", Username = "teste@exemplo.com" }],
            Drives =
            [
                new DriveConfig { DriveId = FakeGraph.DriveId, AccountId = "acc", DisplayName = name1, Kind = kind1 },
                new DriveConfig { DriveId = Id2, AccountId = "acc", DisplayName = name2, Kind = kind2 },
            ],
        }.Save(path);
        Config = AppConfig.Load(path);
        Db = new StateDb(Path.Combine(Dir, "state.db"));
        Engine = new SyncEngine(Config, Db, new ContentCache(Config.CacheDir), new ActivityLog(Path.Combine(Dir, "log.txt")),
            dc => dc.DriveId == Id2 ? R2 : R1);
        Fs = new VirtualFs(Engine);
        Engine.Start();
        Harness.WaitUntil(() => Engine.Drives.All(d => d.LastSync != null) && Engine.Drives.Count() == 2, "carga inicial das duas drives");
    }

    public string Name(string driveId) => Config.Drives.Single(d => d.DriveId == driveId).DisplayName;

    public void Settle()
    {
        Engine.SyncNow();
        Harness.WaitUntil(() => Db.PendingOps() == 0 && Engine.Drives.All(d => !d.Busy), "fila vazia");
        foreach (var d in Engine.Drives) d.RunDeltaAsync(CancellationToken.None).GetAwaiter().GetResult();
        Harness.WaitUntil(() => Db.PendingOps() == 0 && Engine.Drives.All(d => !d.Busy), "fila vazia (2)");
    }

    public void Dispose()
    {
        Engine.Stop();
        Db.Dispose();
        try { Directory.Delete(Dir, true); } catch { }
    }
}

/// <summary>
/// Seção 10 do PENDENCIAS.md com a montagem real: duas drives (EXDEV, raiz virtual, nomes repetidos),
/// RENAME_NOREPLACE/EXCHANGE, nomes que só diferem em maiúsculas ou na normalização Unicode, ENAMETOOLONG e chmod.
/// errno é conferido pelo nome (python <c>errno.errorcode</c>): as mensagens do shell saem em pt-BR.
/// </summary>
[Collection("fuse")]
public class NamesAndDrivesTests
{
    static (int code, string output) Sh(string script, string cwd)
    {
        var psi = new ProcessStartInfo("bash", ["-c", script])
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Trim());
    }

    /// <summary>
    /// Uma chamada de sistema por execução; imprime "OK" (ou o resultado) ou o nome do errno. Argumento com prefixo
    /// "u:" usa escapes \uXXXX (para escrever NFC/NFD sem depender de como o editor guarda o arquivo).
    /// </summary>
    const string OpsPy = """
        import os, sys, errno, ctypes
        libc = ctypes.CDLL(None, use_errno=True)
        def arg(x):
            return x[2:].encode('ascii').decode('unicode_escape') if x.startswith('u:') else x
        op, a = sys.argv[1], [arg(x) for x in sys.argv[2:]]
        try:
            out = 'OK'
            if op == 'create': os.close(os.open(a[0], os.O_WRONLY | os.O_CREAT, 0o644))
            elif op == 'excl': os.close(os.open(a[0], os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o644))
            elif op == 'write':
                fd = os.open(a[0], os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o644); os.write(fd, a[1].encode()); os.close(fd)
            elif op == 'read':
                with open(a[0], 'rb') as f: out = f.read().decode()
            elif op == 'mkdir': os.mkdir(a[0])
            elif op == 'rmdir': os.rmdir(a[0])
            elif op == 'unlink': os.unlink(a[0])
            elif op == 'rename': os.rename(a[0], a[1])
            elif op == 'renameat2':
                if libc.renameat2(-100, os.fsencode(a[0]), -100, os.fsencode(a[1]), int(a[2])) != 0:
                    e = ctypes.get_errno(); raise OSError(e, os.strerror(e))
            elif op == 'stat': os.stat(a[0])
            elif op == 'chmod': os.chmod(a[0], int(a[1], 8))
            elif op == 'mode': out = oct(os.stat(a[0]).st_mode & 0o7777)[2:]
            elif op == 'ls': out = ascii(sorted(os.listdir(a[0])))
            elif op == 'listdir': os.listdir(a[0])
            else: out = 'op?'
            print(out)
        except OSError as e:
            print(errno.errorcode[e.errno])
        """;

    static string Py(string cwd, params string[] args)
    {
        var script = Path.Combine(Path.GetTempPath(), "craftdriver-ops.py");
        if (!File.Exists(script) || File.ReadAllText(script) != OpsPy) File.WriteAllText(script, OpsPy);
        var psi = new ProcessStartInfo("python3", [script, .. args])
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (output + err).Trim();
    }

    static void WithMount(VirtualFs fs, string dir, string firstDrive, Action<string> body)
    {
        var mnt = Path.Combine(dir, "mnt");
        var loop = FuseHost.MountAsync(fs, mnt);
        try
        {
            Harness.WaitUntil(() => Directory.Exists(Path.Combine(mnt, firstDrive)), "montagem visível");
            body(mnt);
        }
        finally
        {
            FuseHost.Unmount();
            Assert.True(loop.Wait(TimeSpan.FromSeconds(15)), "loop do FUSE não terminou após desmontar");
        }
    }

    static void WithMount(Harness h, Action<string> body) =>
        WithMount(h.Fs, h.Dir, Harness.Name, mnt => body(Path.Combine(mnt, Harness.Name)));

    // ---------- 1) duas drives ----------

    /// <summary>
    /// rename(2) entre drives dá EXDEV (são duas nuvens: não há "mover" na Graph entre elas) e o mv do coreutils
    /// cai em copiar+apagar: o conteúdo chega na outra drive e sai da primeira, também na nuvem.
    /// </summary>
    [Fact]
    public void Mv_entre_drives_da_exdev_e_o_mv_copia_e_apaga()
    {
        RealFuse.Require();
        using var t = new TwoDrives(Harness.Name, "Empresa - Documentos");
        t.R1.RemoteCreate("/", "a.txt", "da primeira");
        t.R1.RemoteCreate("/", "Pasta", null, folder: true);
        t.R1.RemoteCreate("/Pasta", "b.txt", "B dentro da pasta");
        t.Settle();
        string d1 = t.Name(FakeGraph.DriveId), d2 = t.Name(TwoDrives.Id2);
        WithMount(t.Fs, t.Dir, d1, mnt =>
        {
            Assert.Equal("EXDEV", Py(mnt, "rename", $"{d1}/a.txt", $"{d2}/a.txt"));
            Assert.Equal("EXDEV", Py(mnt, "rename", $"{d1}/Pasta", $"{d2}/Pasta"));
            Assert.Equal("da primeira", Py(mnt, "read", $"{d1}/a.txt")); // EXDEV não mexeu em nada

            var mv = Sh($"mv '{d1}/a.txt' '{d2}/' && mv '{d1}/Pasta' '{d2}/'", mnt);
            Assert.True(mv.code == 0, mv.output);
            Assert.Equal("da primeira", Py(mnt, "read", $"{d2}/a.txt"));
            Assert.Equal("B dentro da pasta", Py(mnt, "read", $"{d2}/Pasta/b.txt"));
            Assert.Equal("ENOENT", Py(mnt, "stat", $"{d1}/a.txt"));
            Assert.Equal("ENOENT", Py(mnt, "stat", $"{d1}/Pasta"));

            t.Settle();
            Assert.Equal("da primeira", t.R2.ReadText("/a.txt"));
            Assert.Equal("B dentro da pasta", t.R2.ReadText("/Pasta/b.txt"));
            Assert.Empty(t.R1.ListPaths());
            Assert.Equal(0, t.R1.Moves + t.R2.Moves); // nada de "move" cruzando drives na Graph
        });
    }

    /// <summary>
    /// A raiz da montagem só tem as pastas das drives: criar/renomear para ela dá EACCES (não há drive onde
    /// guardar), apagar/renomear/chmod a pasta de uma drive dá EPERM; nada vai para a fila.
    /// </summary>
    [Fact]
    public void Raiz_virtual_lista_as_drives_e_recusa_alteracoes()
    {
        RealFuse.Require();
        using var t = new TwoDrives(Harness.Name, "Empresa - Documentos");
        t.R1.RemoteCreate("/", "a.txt", "A");
        t.Settle();
        string d1 = t.Name(FakeGraph.DriveId), d2 = t.Name(TwoDrives.Id2);
        WithMount(t.Fs, t.Dir, d1, mnt =>
        {
            Assert.Equal($"{d2}\n{d1}\n{d1}{SyncEngine.SharedContainerSuffix}", Sh("LC_ALL=C ls -1", mnt).output);
            Assert.Equal("EACCES", Py(mnt, "create", "solto.txt"));
            Assert.Equal("EACCES", Py(mnt, "mkdir", "Nova pasta"));
            Assert.Equal("EACCES", Py(mnt, "rename", $"{d1}/a.txt", "a.txt"));
            Assert.Equal("EPERM", Py(mnt, "rmdir", d2));
            Assert.Equal("EPERM", Py(mnt, "rename", d1, "Outro nome"));
            Assert.Equal("EPERM", Py(mnt, "rename", d1, $"{d2}/{d1}"));
            Assert.Equal("EPERM", Py(mnt, "chmod", ".", "700"));
            Assert.Equal($"{d2}\n{d1}\n{d1}{SyncEngine.SharedContainerSuffix}", Sh("LC_ALL=C ls -1", mnt).output);
            Assert.Equal("A", Py(mnt, "read", $"{d1}/a.txt"));
            Assert.Equal(0, t.Db.PendingOps());
        });
        Assert.Equal(["/a.txt"], t.R1.ListPaths());
        Assert.Equal(0, t.R1.Uploads + t.R1.Deletes + t.R1.Moves + t.R2.Uploads + t.R2.Deletes + t.R2.Moves);
    }

    /// <summary>
    /// Duas bibliotecas com o mesmo nome (ex.: "Documentos" de dois sites): a configuração carregada dá nomes
    /// distintos e cada pasta da montagem mostra a SUA drive (antes só a primeira seria alcançável).
    /// </summary>
    [Fact]
    public void Drives_com_o_mesmo_nome_aparecem_com_nomes_distintos()
    {
        RealFuse.Require();
        using var t = new TwoDrives("Empresa - Documentos", "Empresa - Documentos", kind1: "documentLibrary");
        Assert.Equal("Empresa - Documentos", t.Name(FakeGraph.DriveId));
        Assert.Equal("Empresa - Documentos (2)", t.Name(TwoDrives.Id2));
        Assert.NotNull(t.Config.LoadWarning);
        t.R1.RemoteCreate("/", "um.txt", "da drive 1");
        t.R2.RemoteCreate("/", "dois.txt", "da drive 2");
        t.Settle();
        WithMount(t.Fs, t.Dir, "Empresa - Documentos", mnt =>
        {
            Assert.Equal("Empresa - Documentos\nEmpresa - Documentos (2)", Sh("LC_ALL=C ls -1", mnt).output);
            Assert.Equal("um.txt", Sh("ls -1 'Empresa - Documentos'", mnt).output);
            Assert.Equal("dois.txt", Sh("ls -1 'Empresa - Documentos (2)'", mnt).output);
            Assert.Equal("da drive 2", Py(mnt, "read", "Empresa - Documentos (2)/dois.txt"));
            Assert.Equal("OK", Py(mnt, "write", "Empresa - Documentos (2)/novo.txt", "vai para a 2"));
        });
        t.Settle();
        Assert.Equal("vai para a 2", t.R2.ReadText("/novo.txt"));
        Assert.Equal(["/um.txt"], t.R1.ListPaths());
    }

    // ---------- 2) RENAME_NOREPLACE / RENAME_EXCHANGE ----------

    /// <summary>
    /// renameat2(RENAME_NOREPLACE) sobre destino existente: EEXIST, nada muda (o kernel checa pelo dentry; o
    /// VirtualFs checa de novo sob o lock do banco, para o caso de o destino ter chegado da nuvem no meio).
    /// RENAME_EXCHANGE não tem equivalente na Graph (troca atômica de dois itens): EINVAL, como um sistema de
    /// arquivos que não suporta a flag — e nenhum dos dois arquivos é tocado.
    /// </summary>
    [Fact]
    public void Rename_noreplace_e_exchange_nao_tocam_no_destino()
    {
        RealFuse.Require();
        using var h = new Harness();
        var a = h.Remote.RemoteCreate("/", "a.txt", "conteúdo A");
        var b = h.Remote.RemoteCreate("/", "b.txt", "conteúdo B");
        h.Settle();
        WithMount(h, root =>
        {
            Assert.Equal("EEXIST", Py(root, "renameat2", "a.txt", "b.txt", "1"));
            Assert.Equal("conteúdo B", Py(root, "read", "b.txt"));
            Assert.Equal("conteúdo A", Py(root, "read", "a.txt"));
            Assert.Equal("EINVAL", Py(root, "renameat2", "a.txt", "b.txt", "2"));
            Assert.Equal("conteúdo B", Py(root, "read", "b.txt"));
            Assert.Equal("conteúdo A", Py(root, "read", "a.txt"));
            Sh("mv -n a.txt b.txt", root); // coreutils 9.x: -n pula (código de saída varia entre versões)
            Assert.Equal("conteúdo B", Py(root, "read", "b.txt"));
            Assert.Equal("conteúdo A", Py(root, "read", "a.txt"));
            // sem destino, NOREPLACE renomeia normalmente
            Assert.Equal("OK", Py(root, "renameat2", "a.txt", "c.txt", "1"));
            Assert.Equal("conteúdo A", Py(root, "read", "c.txt"));
            Assert.Equal("ENOENT", Py(root, "stat", "a.txt"));
        });
        // destino que apareceu depois do lookup do kernel (ex.: veio da nuvem): o VirtualFs também recusa
        Assert.Equal(-Errno.EEXIST, h.Fs.Rename(h.P("/c.txt"), h.P("/b.txt"), VirtualFs.RenameNoReplace));
        h.Settle();
        Assert.Equal(["/b.txt", "/c.txt"], h.Remote.ListPaths());
        Assert.Equal("conteúdo B", h.Remote.ReadText("/b.txt"));
        Assert.Equal(b.Id, h.Remote.FindByPath("/b.txt")!.Id);
        Assert.Equal(a.Id, h.Remote.FindByPath("/c.txt")!.Id); // renomeado pelo id, não reenviado
        Assert.Equal(0, h.Remote.Uploads + h.Remote.Deletes);
    }

    // ---------- 3) maiúsculas ----------

    /// <summary>
    /// O OneDrive não diferencia maiúsculas (inclusive acentuadas: "AÇÃO" = "ação"); o Linux sim. Decisão: o lookup
    /// é exato (como no Linux: "A.txt" não existe se só há "a.txt"), mas criar/mkdir/renomear para um nome que a
    /// nuvem consideraria igual a OUTRO item dá EEXIST — nunca dois itens que colidiriam na nuvem, nunca
    /// sobrescrever em silêncio o arquivo de outro nome. Renomear só a caixa do próprio item funciona (mesmo id).
    /// </summary>
    [Fact]
    public void Nomes_que_so_diferem_em_maiusculas()
    {
        RealFuse.Require();
        using var h = new Harness();
        var a = h.Remote.RemoteCreate("/", "a.txt", "original");
        h.Remote.RemoteCreate("/", "docs", null, folder: true);
        h.Remote.RemoteCreate("/", "ação.txt", "acentuado");
        h.Remote.RemoteCreate("/", "época", null, folder: true);
        h.Settle();
        WithMount(h, root =>
        {
            Assert.Equal("ENOENT", Py(root, "stat", "A.txt"));
            Assert.Equal("EEXIST", Py(root, "excl", "A.txt"));
            Assert.Equal("EEXIST", Py(root, "create", "A.txt"));
            Assert.Equal("EEXIST", Py(root, "write", "A.txt", "não pode sobrescrever o a.txt"));
            Assert.Equal("original", Py(root, "read", "a.txt"));
            Assert.Equal("EEXIST", Py(root, "mkdir", "Docs"));
            Assert.Equal("EEXIST", Py(root, "mkdir", "A.TXT"));
            // fora do ASCII (o NOCASE do SQLite só dobra A-Z)
            Assert.Equal("EEXIST", Py(root, "create", "AÇÃO.txt"));
            Assert.Equal("EEXIST", Py(root, "mkdir", "ÉPOCA"));
            Assert.Equal("acentuado", Py(root, "read", "ação.txt"));

            Assert.Equal("OK", Py(root, "write", "b.txt", "B"));
            Assert.Equal("EEXIST", Py(root, "rename", "b.txt", "A.txt"));
            Assert.Equal("EEXIST", Py(root, "rename", "b.txt", "Ação.txt"));
            Assert.Equal("B", Py(root, "read", "b.txt"));

            // só a caixa do próprio item: é o mesmo item, renomeia
            Assert.Equal("OK", Py(root, "rename", "a.txt", "A.txt"));
            Assert.Equal("OK", Py(root, "rename", "docs", "Docs"));
            Assert.Equal("OK", Py(root, "rename", "ação.txt", "Ação.txt"));
            Assert.Equal("['A.txt', 'Ação.txt', 'Docs', 'b.txt', 'época']".Replace("ç", "\\xe7").Replace("ã", "\\xe3").Replace("é", "\\xe9"),
                Py(root, "ls", "."));
            Assert.Equal("original", Py(root, "read", "A.txt"));
        });
        h.Settle();
        Assert.Equal(["/A.txt", "/Ação.txt", "/Docs", "/b.txt", "/época"], h.Remote.ListPaths().OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(a.Id, h.Remote.FindByPath("/A.txt")!.Id);
        Assert.Equal("original", h.Remote.ReadText("/A.txt"));
        Assert.Equal(1, h.Remote.Uploads); // só o b.txt
    }

    // ---------- 4) Unicode NFC/NFD ----------

    const string NfcRelatorio = "relat\u00f3rio.txt", NfdAgua = "a\u0301gua.txt", NfcAgua = "\u00e1gua.txt";

    /// <summary>
    /// A Microsoft não documenta se o OneDrive/SharePoint considera NFC e NFD iguais (o suporte do SharePoint
    /// descreve nomes NFD vindos do macOS aparecendo decompostos no Windows: o servidor guarda os bytes como
    /// recebeu). Decisão (a mais segura): o nome sobe exatamente como foi criado, sem normalizar; o lookup é exato;
    /// e criar um nome canonicamente equivalente a outro item da mesma pasta dá EEXIST (se a nuvem os considerar
    /// iguais, os dois colidiriam; se não, o custo é só não poder ter dois nomes visualmente idênticos).
    /// </summary>
    [Fact]
    public void Nomes_nfc_e_nfd()
    {
        RealFuse.Require();
        using var h = new Harness();
        var rel = h.Remote.RemoteCreate("/", NfcRelatorio, "conteúdo NFC");
        h.Settle();
        WithMount(h, root =>
        {
            Assert.Equal("['relat\\xf3rio.txt']", Py(root, "ls", "."));
            Assert.Equal("conteúdo NFC", Py(root, "read", "u:relat\\u00f3rio.txt"));
            Assert.Equal("OK", Py(root, "rename", "u:relat\\u00f3rio.txt", "u:Relat\\u00f3rio final.txt"));
            Assert.Equal("conteúdo NFC", Py(root, "read", "u:Relat\\u00f3rio final.txt"));

            Assert.Equal("OK", Py(root, "write", "u:a\\u0301gua.txt", "criado em NFD"));
            Assert.Equal("EEXIST", Py(root, "excl", "u:\\u00e1gua.txt"));
            Assert.Equal("EEXIST", Py(root, "mkdir", "u:\\u00e1gua.txt"));
            Assert.Equal("EEXIST", Py(root, "rename", "u:Relat\\u00f3rio final.txt", "u:\\u00e1gua.txt"));
            Assert.Equal("['Relat\\xf3rio final.txt', 'a\\u0301gua.txt']", Py(root, "ls", "."));
            Assert.Equal("criado em NFD", Py(root, "read", "u:a\\u0301gua.txt"));
        });
        h.Settle();
        var paths = h.Remote.ListPaths();
        Assert.Equal(["/Relat\u00f3rio final.txt", "/" + NfdAgua], paths.OrderBy(x => x, StringComparer.Ordinal));
        var agua = paths.Single(p => p != "/Relat\u00f3rio final.txt")[1..];
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(NfdAgua), System.Text.Encoding.UTF8.GetBytes(agua)); // sem perder bytes
        Assert.Equal(rel.Id, h.Remote.FindByPath("/Relat\u00f3rio final.txt")!.Id);
        Assert.Equal(1, h.Remote.Uploads);
        Assert.DoesNotContain(paths, p => p.Contains(NfcAgua));
    }

    // ---------- 5) ENAMETOOLONG ----------

    /// <summary>
    /// NAME_MAX do Linux é 255 BYTES (o statfs da montagem informa 255): 128 "á" são 128 caracteres mas 256 bytes.
    /// O kernel só barra acima de 1024 (FUSE_NAME_MAX); o VirtualFs tem que contar bytes UTF-8.
    /// </summary>
    [Fact]
    public void Nome_acima_de_255_bytes_da_enametoolong()
    {
        RealFuse.Require();
        using var h = new Harness();
        string ok = new string('á', 127) + "x", longo = new('á', 128), ascii = new('a', 256);
        Assert.Equal(255, System.Text.Encoding.UTF8.GetByteCount(ok));
        WithMount(h, root =>
        {
            Assert.Equal("OK", Py(root, "write", ok, "cabe"));
            Assert.Equal("OK", Py(root, "mkdir", "pasta"));
            foreach (var name in new[] { longo, ascii, "pasta/" + longo })
            {
                Assert.Equal("ENAMETOOLONG", Py(root, "create", name));
                Assert.Equal("ENAMETOOLONG", Py(root, "mkdir", name));
                Assert.Equal("ENAMETOOLONG", Py(root, "rename", ok, name));
            }
            Assert.Equal("cabe", Py(root, "read", ok));
            Assert.Equal("255", Sh("stat -f -c %l .", root).output);
        });
        h.Settle();
        Assert.Equal(["/pasta", "/" + ok], h.Remote.ListPaths().OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// O OneDrive/SharePoint recusa caminhos com mais de 400 caracteres (pathIsTooLong) — e o prefixo do site/da
    /// biblioteca conta, então o limite real não é conhecido aqui; a nuvem é quem decide. A pasta recusada vai para
    /// Problemas, e o que está dentro dela também (falha permanente com mensagem), em vez de ficar tentando de
    /// novo para sempre com "a pasta ainda não foi criada" e travar a fila da drive inteira.
    /// </summary>
    [Fact]
    public void Caminho_longo_demais_para_a_nuvem_vai_para_problemas_sem_travar_a_fila()
    {
        RealFuse.Require();
        using var h = new Harness();
        h.Remote.MaxPathChars = 400;
        var seg = new string('p', 59); // "/" + 59 = 60 por nível: o 7º nível passa de 400
        var deep = string.Join("/", Enumerable.Range(1, 8).Select(i => $"{i}{seg}"));
        WithMount(h, root =>
        {
            var r = Sh($"mkdir -p '{deep}' && echo fundo > '{deep}/arquivo.txt' && echo raso > ok.txt", root);
            Assert.True(r.code == 0, r.output);
        });
        h.Settle(); // terminar já prova que a fila não ficou presa
        Assert.Equal("raso\n", h.Remote.ReadText("/ok.txt"));
        var failures = h.Db.Failures();
        Assert.Contains(failures, f => f.Name == $"7{seg}" && f.Error.Contains("maximum length"));
        Assert.Contains(failures, f => f.Name == $"8{seg}" && f.Error.Contains($"7{seg}"));
        Assert.Contains(failures, f => f.Name == "arquivo.txt" && f.Error.Contains($"8{seg}"));
        Assert.Equal(3, failures.Count);
        Assert.Equal(6, h.Remote.ListPaths().Count(p => p.StartsWith("/1")));
        Assert.Equal(1, h.Remote.PathTooLongRejections);
    }

    // ---------- 6) chmod ----------

    /// <summary>
    /// Complementa BehaviorTests/FuseDataLossTests (arquivo +x/600, mkdir -m): chmod em PASTA (inclusive 000,
    /// que o kernel passa a respeitar com default_permissions), em arquivo só-online sem baixá-lo, e o modo
    /// continua depois de desmontar/montar e de a pasta ser renomeada na nuvem. Nada disso vai para a nuvem.
    /// </summary>
    [Fact]
    public void Chmod_em_pasta_e_em_somente_online_persiste_entre_montagens()
    {
        RealFuse.RequireNonRoot();
        using var h = new Harness();
        h.Remote.RemoteCreate("/", "Pasta", null, folder: true);
        h.Remote.RemoteCreate("/Pasta", "dentro.txt", "conteúdo de dentro");
        h.Remote.RemoteCreate("/", "nuvem.txt", "só online");
        h.Settle();
        WithMount(h, root =>
        {
            Assert.Equal("OK", Py(root, "chmod", "Pasta", "000"));
            Assert.Equal("0", Py(root, "mode", "Pasta"));
            Assert.Equal("EACCES", Py(root, "listdir", "Pasta"));
            Assert.Equal("OK", Py(root, "chmod", "Pasta", "700"));
            Assert.Equal("700", Py(root, "mode", "Pasta"));
            Assert.Equal("['dentro.txt']", Py(root, "ls", "Pasta"));

            Assert.Equal(0, Sh("chmod +x nuvem.txt", root).code);
            Assert.Equal("755", Py(root, "mode", "nuvem.txt"));
            Assert.Equal("OK", Py(root, "chmod", "nuvem.txt", "000"));
            Assert.Equal("EACCES", Py(root, "read", "nuvem.txt"));
            Assert.Equal("OK", Py(root, "chmod", "nuvem.txt", "640"));
            Assert.Equal(0, h.Remote.Downloads); // chmod não baixa
            Assert.Equal("OnlineOnly", Sh("getfattr --only-values -n user.onedrive.status nuvem.txt", root).output);
        });
        WithMount(h, root =>
        {
            Assert.Equal("640", Py(root, "mode", "nuvem.txt"));
            Assert.Equal("700", Py(root, "mode", "Pasta"));
        });
        h.Remote.RemoteRename("/Pasta", "Pasta renomeada");
        h.Settle();
        WithMount(h, root =>
        {
            Assert.Equal("700", Py(root, "mode", "Pasta renomeada"));
            Assert.Equal("640", Py(root, "mode", "nuvem.txt"));
            Assert.Equal("só online", Py(root, "read", "nuvem.txt"));
        });
        Assert.Equal(0, h.Remote.Uploads + h.Remote.Moves + h.Remote.Deletes);
        Assert.Equal(0, h.Db.PendingOps());
    }
}
