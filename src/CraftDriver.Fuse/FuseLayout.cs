using System.Runtime.InteropServices;

namespace CraftDriver.Fuse;

/// <summary>
/// Offsets das structs da glibc/libfuse que o <see cref="FuseHost"/> preenche à mão. Medidos com
/// tools/fuse-layout/probe.c (offsetof nos cabeçalhos reais); <c>FuseLayoutTests</c> compara esta tabela com a
/// medição da máquina em que roda (x86_64 aqui; aarch64 no executor ARM do CI).
/// </summary>
public sealed record FuseLayout(
    int StatSize, int StNlink, int StMode, int StUid, int StGid, bool BlksizeIs32Bit,
    int EntrySize, int EntryAttrTimeout, int EntryEntryTimeout)
{
    // campos iguais nas duas arquiteturas e em libfuse 3.14–3.18
    public const int StIno = 8, StSize = 48, StBlksize = 56, StBlocks = 64, StAtim = 72, StMtim = 88, StCtim = 104;
    public const int EntryAttr = 16, FiFh = 16, FiBits = 4, FiDirectIo = 0x2, FiKeepCache = 0x4;

    /// <summary>
    /// Bytes de fuse_file_info copiados do que a libfuse entrega: o prefixo comum (flags, bits, fh, lock_owner,
    /// poll_events). A struct tem 40 bytes na 3.14 (Ubuntu 24.04, Debian 12) e 64 na 3.17+; copiar 64 de uma de 40 lia
    /// além do fim. O resto da cópia fica zerado (valores padrão dos campos novos).
    /// </summary>
    public const int FileInfoCopy = 40;

    public const int FileInfoAlloc = 64;

    public static readonly FuseLayout X64 = new(144, 16, 24, 28, 32, false, 176, 160, 168);
    /// <summary>aarch64 (asm-generic): stat de 128 bytes, então attr_timeout/entry_timeout ficam 16 bytes antes.</summary>
    public static readonly FuseLayout Arm64 = new(128, 20, 16, 24, 28, true, 160, 144, 152);

    public static FuseLayout For(Architecture arch) => arch == Architecture.Arm64 ? Arm64 : X64;
    public static readonly FuseLayout Current = For(RuntimeInformation.ProcessArchitecture);
}
