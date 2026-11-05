#define FUSE_USE_VERSION 34
#include <fuse_lowlevel.h>
#include <sys/stat.h>
#include <sys/statvfs.h>
#include <stddef.h>

/* Offsets das structs da glibc/libfuse que o FuseHost preenche à mão (ver src/CraftDriver.Fuse/FuseLayout.cs).
 * Uso: gcc $(pkg-config --cflags fuse3) -S -o - probe.c | grep -o '@@[a-z_]* [#$]*[0-9]*'
 * Funciona em compilação cruzada (aarch64-linux-gnu-gcc) porque não precisa executar nada. */

/* Valores em tempo de compilação, emitidos no assembly como "@@nome valor" (funciona em compilação cruzada). */
#define P(name, val) __asm__ volatile("\n@@" #name " %c0\n" :: "i"((long)(val)))
#define OPS(f) P(ops_##f, offsetof(struct fuse_lowlevel_ops, f) / sizeof(void *))

void probe(void)
{
    P(stat_size, sizeof(struct stat));
    P(st_ino, offsetof(struct stat, st_ino));
    P(st_nlink, offsetof(struct stat, st_nlink));
    P(st_mode, offsetof(struct stat, st_mode));
    P(st_uid, offsetof(struct stat, st_uid));
    P(st_gid, offsetof(struct stat, st_gid));
    P(st_size, offsetof(struct stat, st_size));
    P(st_blksize, offsetof(struct stat, st_blksize));
    P(st_blksize_size, sizeof(((struct stat *)0)->st_blksize));
    P(st_blocks, offsetof(struct stat, st_blocks));
    P(st_atim, offsetof(struct stat, st_atim));
    P(st_mtim, offsetof(struct stat, st_mtim));
    P(st_ctim, offsetof(struct stat, st_ctim));
    P(statvfs_size, sizeof(struct statvfs));
    P(f_bsize, offsetof(struct statvfs, f_bsize));
    P(f_blocks, offsetof(struct statvfs, f_blocks));
    P(f_bavail, offsetof(struct statvfs, f_bavail));
    P(f_files, offsetof(struct statvfs, f_files));
    P(f_namemax, offsetof(struct statvfs, f_namemax));
    P(fi_size, sizeof(struct fuse_file_info));
    P(fi_fh, offsetof(struct fuse_file_info, fh));
    P(entry_size, sizeof(struct fuse_entry_param));
    P(entry_attr, offsetof(struct fuse_entry_param, attr));
    P(entry_attr_timeout, offsetof(struct fuse_entry_param, attr_timeout));
    P(entry_entry_timeout, offsetof(struct fuse_entry_param, entry_timeout));
    P(ctx_pid, offsetof(struct fuse_ctx, pid));
    P(timespec_size, sizeof(struct timespec));
    OPS(init); OPS(lookup); OPS(forget); OPS(getattr); OPS(setattr); OPS(mkdir); OPS(unlink); OPS(rmdir);
    OPS(rename); OPS(open); OPS(read); OPS(write); OPS(flush); OPS(release); OPS(fsync); OPS(opendir);
    OPS(readdir); OPS(releasedir); OPS(statfs); OPS(setxattr); OPS(getxattr); OPS(listxattr); OPS(removexattr);
    OPS(access); OPS(create); OPS(readdirplus); OPS(copy_file_range); OPS(lseek);
}

