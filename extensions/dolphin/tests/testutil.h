// Utilidades comuns dos testes: diretório com user xattrs e gravação do status.
#pragma once

#include "craftdriverstatus.h"

#include <QDir>
#include <QFile>
#include <QTemporaryDir>

#include <memory>
#include <sys/xattr.h>

namespace TestUtil
{
inline std::unique_ptr<QTemporaryDir> makeDir()
{
    QString base = qEnvironmentVariable("CRAFTDRIVER_TEST_DIR");
    if (base.isEmpty()) {
        base = QDir::tempPath();
    }
    return std::make_unique<QTemporaryDir>(base + QStringLiteral("/craftdriver-dolphin-XXXXXX"));
}

inline bool setStatus(const QString &path, const QString &status)
{
    const QByteArray v = status.toUtf8();
    return lsetxattr(QFile::encodeName(path).constData(), CraftDriver::StatusXattr, v.constData(), v.size(), 0) == 0;
}

inline QString touch(const QDir &dir, const QString &name, const QByteArray &content = "x")
{
    QFile f(dir.filePath(name));
    if (f.open(QIODevice::WriteOnly)) {
        f.write(content);
    }
    return f.fileName();
}
}
