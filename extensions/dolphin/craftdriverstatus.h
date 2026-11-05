// Leitura do status do craftDriver (xattr do FUSE) e escolha do emblema.
#pragma once

#include <QString>

namespace CraftDriver
{
// Atributo exposto pelo FUSE do craftDriver em cada item (valores = enum ItemStatus em
// src/CraftDriver.Core/Vfs/VirtualFs.cs: OnlineOnly, Available, AlwaysAvailable, Syncing, LocalOnly, Folder).
inline constexpr char StatusXattr[] = "user.onedrive.status";

// Valor de user.onedrive.status, ou string vazia se o arquivo não tem o atributo (não está no craftDriver).
// Não segue links simbólicos. Em erro, *err recebe o errno (ENODATA/ENOTSUP = fora do craftDriver).
QString readStatus(const QString &localPath, int *err = nullptr);

// Nome do ícone do emblema para um status (vazio = sem emblema, ex. "Folder" ou valor desconhecido).
QString emblemFor(const QString &status);
}
