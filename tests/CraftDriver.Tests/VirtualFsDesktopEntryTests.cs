using System.Diagnostics;
using System.Text;
using CraftDriver.Core.Vfs;
using CraftDriver.Fuse;

namespace CraftDriver.Tests;

[Collection("fuse")]
public class VirtualFsDesktopEntryTests
{
    [Fact]
    public void Raiz_virtual_expoe_arquivo_directory_com_icone_folder_cloud()
    {
        using var h = new Harness();
        var fs = h.Fs;

        // 1. GetAttr na raiz virtual /.directory
        var r = fs.GetAttr("/.directory", out var attr);
        Assert.Equal(0, r);
        Assert.False(attr.IsDir);
        Assert.True(attr.Size > 0);

        // 2. ReadDir na raiz virtual lista o .directory
        var entries = new List<(string name, FileAttr attr)>();
        r = fs.ReadDir("/", entries);
        Assert.Equal(0, r);
        Assert.Contains(entries, e => e.name == ".directory");

        // 3. Open e Read em /.directory
        r = fs.Open("/.directory", OpenFlags.AccMode & 0, 1234, out var fh);
        Assert.Equal(0, r);
        try
        {
            var buf = new byte[256];
            var bytesRead = fs.Read(fh, buf, 0);
            Assert.True(bytesRead > 0);
            var content = Encoding.UTF8.GetString(buf, 0, bytesRead);
            Assert.Contains("Icon=folder-cloud", content);
            Assert.Contains("[Desktop Entry]", content);
        }
        finally
        {
            fs.Release(fh);
        }

        // 4. Tentativa de escrita em /.directory deve ser rejeitada
        r = fs.Open("/.directory", OpenFlags.WrOnly, 1234, out _);
        Assert.Equal(-Errno.EACCES, r);

        // 5. Tentativa de unlink em /.directory deve ser rejeitada
        r = fs.Unlink("/.directory");
        Assert.Equal(-Errno.EPERM, r);

        // 6. Tentativa de rename de/para /.directory deve ser rejeitada
        r = fs.Rename("/.directory", "/outro", 0);
        Assert.Equal(-Errno.EPERM, r);
    }

    [Fact]
    public async Task Montagem_fuse_com_fsname_dev_craftdriver_permite_dolphin_ler_icone_folder_cloud()
    {
        RealFuse.Require();
        using var h = new Harness();
        var mountDir = Path.Combine(h.Dir, "mount");
        Directory.CreateDirectory(mountDir);

        var loop = FuseHost.MountAsync(h.Fs, mountDir);
        try
        {
            var root = Path.Combine(mountDir, Harness.Name);
            Harness.WaitUntil(() => Directory.Exists(root), "montagem visivel");

            if (!File.Exists("/tmp/test_kf6_icon/build/check_icon"))
            {
                return;
            }

            var psi = new ProcessStartInfo("/tmp/test_kf6_icon/build/check_icon", [mountDir])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var outText = await p.StandardOutput.ReadToEndAsync();
            var errText = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            var combined = outText + errText;
            Assert.Contains("iconName: \"folder-cloud\"", combined);
        }
        finally
        {
            FuseHost.Unmount();
            await loop;
        }
    }
}
