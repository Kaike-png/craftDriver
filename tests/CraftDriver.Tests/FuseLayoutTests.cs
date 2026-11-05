using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CraftDriver.Fuse;

namespace CraftDriver.Tests;

/// <summary>
/// O FuseHost escreve struct stat, fuse_entry_param e fuse_file_info byte a byte: os offsets de FuseLayout têm de
/// bater com os cabeçalhos reais. Valores abaixo medidos com tools/fuse-layout/probe.c (offsetof) em 2026-11-05:
/// Arch x86_64 com libfuse 3.18.3, debian:12 x86_64 com libfuse 3.14 e debian:12 aarch64 (cruzado) com libfuse 3.14.
/// </summary>
public class FuseLayoutTests
{
    // stat_size, st_nlink, st_mode, st_uid, st_gid, st_blksize (bytes), entry_size, entry_attr_timeout, entry_entry_timeout, fi_size
    static readonly Dictionary<string, int[]> Measured = new()
    {
        ["x86_64 libfuse 3.18"] = [144, 16, 24, 28, 32, 8, 176, 160, 168, 64],
        ["x86_64 libfuse 3.14"] = [144, 16, 24, 28, 32, 8, 176, 160, 168, 40],
        ["aarch64 libfuse 3.14"] = [128, 20, 16, 24, 28, 4, 160, 144, 152, 40],
    };

    static void Check(FuseLayout l, int[] m, string where)
    {
        Assert.True(l.StatSize == m[0], $"{where}: sizeof(struct stat) {m[0]}, tabela {l.StatSize}");
        Assert.True(l.StNlink == m[1] && l.StMode == m[2] && l.StUid == m[3] && l.StGid == m[4], $"{where}: nlink/mode/uid/gid");
        Assert.True(l.BlksizeIs32Bit == (m[5] == 4), $"{where}: tamanho de st_blksize");
        Assert.True(l.EntrySize == m[6], $"{where}: sizeof(fuse_entry_param) {m[6]}, tabela {l.EntrySize}");
        Assert.True(l.EntryAttrTimeout == m[7] && l.EntryEntryTimeout == m[8],
            $"{where}: attr_timeout/entry_timeout em {m[7]}/{m[8]}, tabela {l.EntryAttrTimeout}/{l.EntryEntryTimeout}");
        Assert.True(FuseLayout.FileInfoCopy <= m[9], $"{where}: copia {FuseLayout.FileInfoCopy} bytes de uma fuse_file_info de {m[9]}");
        Assert.True(FuseLayout.FileInfoAlloc >= 64, "a cópia alocada precisa caber a maior fuse_file_info (3.17+)");
    }

    [Fact]
    public void Tabela_bate_com_as_medicoes_de_cada_arquitetura_e_libfuse()
    {
        Check(FuseLayout.X64, Measured["x86_64 libfuse 3.18"], "x86_64 3.18");
        Check(FuseLayout.X64, Measured["x86_64 libfuse 3.14"], "x86_64 3.14");
        Check(FuseLayout.Arm64, Measured["aarch64 libfuse 3.14"], "aarch64 3.14");
    }

    /// <summary>Mede nesta máquina (gcc + cabeçalhos da fuse3): no executor ARM do CI confere o aarch64 de verdade.</summary>
    [Fact]
    public void Tabela_bate_com_os_cabecalhos_desta_maquina()
    {
        var probe = Path.Combine(TestSupport.RepoRoot, "tools", "fuse-layout", "probe.c");
        Assert.SkipUnless(File.Exists("/usr/include/fuse3/fuse_lowlevel.h") && Which("gcc"), "sem gcc ou cabeçalhos da fuse3");
        var psi = new ProcessStartInfo("gcc", ["-I/usr/include/fuse3", "-S", "-O0", "-o", "-", probe]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var asm = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "gcc falhou: " + err);
        var v = Regex.Matches(asm, @"@@([a-z_]+) [#$]?(-?\d+)").ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value));
        int[] m = [v["stat_size"], v["st_nlink"], v["st_mode"], v["st_uid"], v["st_gid"], v["st_blksize_size"],
            v["entry_size"], v["entry_attr_timeout"], v["entry_entry_timeout"], v["fi_size"]];
        Check(FuseLayout.Current, m, RuntimeInformation.ProcessArchitecture.ToString());
        // o que é igual em todas: confere também
        Assert.Equal([FuseLayout.StIno, FuseLayout.StSize, FuseLayout.StBlksize, FuseLayout.StBlocks, FuseLayout.StAtim, FuseLayout.StMtim, FuseLayout.StCtim, FuseLayout.EntryAttr, FuseLayout.FiFh],
            [v["st_ino"], v["st_size"], v["st_blksize"], v["st_blocks"], v["st_atim"], v["st_mtim"], v["st_ctim"], v["entry_attr"], v["fi_fh"]]);
        Assert.Equal(80, v["f_namemax"]); // statvfs: u[10] no FuseHost
        Assert.Equal(112, v["statvfs_size"]);
        // índices de fuse_lowlevel_ops usados por FuseHost.OpIndex
        foreach (var op in new[] { "init", "lookup", "forget", "getattr", "setattr", "mkdir", "unlink", "rmdir", "rename", "open", "read",
                     "write", "flush", "release", "fsync", "opendir", "readdir", "releasedir", "statfs", "setxattr", "getxattr",
                     "listxattr", "removexattr", "access", "create", "readdirplus", "copy_file_range", "lseek" })
            Assert.True(FuseHost.OpIndexOf(op) == v["ops_" + op], $"fuse_lowlevel_ops.{op}: {v["ops_" + op]}, FuseHost {FuseHost.OpIndexOf(op)}");
    }

    static bool Which(string cmd) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, cmd)));
}
