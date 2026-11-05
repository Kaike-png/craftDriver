#include "craftdriverstatus.h"

#include <QFile>

#include <cerrno>
#include <sys/xattr.h>

namespace CraftDriver
{
QString readStatus(const QString &localPath, int *err)
{
    const QByteArray path = QFile::encodeName(localPath);
    char buf[128];
    const ssize_t n = lgetxattr(path.constData(), StatusXattr, buf, sizeof(buf));
    if (n < 0) {
        if (err) {
            *err = errno;
        }
        return {};
    }
    if (err) {
        *err = 0;
    }
    QString value = QString::fromUtf8(buf, n);
    value.remove(QChar(0));
    return value.trimmed();
}

QString emblemFor(const QString &status)
{
    // Nomes que existem no Breeze (tema padrão do Plasma). Não dá para escolher entre candidatos com
    // QIcon::hasThemeIcon: o Qt faz fallback tirando o sufixo depois do "-" ("emblem-cloud" → "emblem")
    // e responde "existe" para quase tudo. Em outros temas, o mesmo fallback acha algo parecido.
    //   folder-cloud            nuvem vazada            → só na nuvem
    //   emblem-default-symbolic "✓" fino                → disponível neste dispositivo (como o OneDrive)
    //   vcs-normal              círculo verde com "✓"   → sempre disponível (fixado)
    //   vcs-update-required     seta circular laranja   → sincronizando
    //   emblem-unavailable      "X" cinza               → somente local (não sincroniza)
    if (status == QLatin1String("OnlineOnly")) {
        return QStringLiteral("folder-cloud");
    }
    if (status == QLatin1String("Available")) {
        return QStringLiteral("emblem-default-symbolic");
    }
    if (status == QLatin1String("AlwaysAvailable")) {
        return QStringLiteral("vcs-normal");
    }
    if (status == QLatin1String("Syncing")) {
        return QStringLiteral("vcs-update-required");
    }
    if (status == QLatin1String("LocalOnly")) {
        return QStringLiteral("emblem-unavailable");
    }
    return {}; // "Folder" e valores desconhecidos: sem emblema
}
}
