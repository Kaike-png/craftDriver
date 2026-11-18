#include "craftdriverstatus.h"

#include <QDir>
#include <QFile>
#include <QRegularExpression>

#include <cerrno>
#include <climits>
#include <cstdlib>
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

QStringList craftdriverMounts()
{
    QString file = qEnvironmentVariable("CRAFTDRIVER_MOUNTS_FILE");
    if (file.isEmpty()) {
        file = QStringLiteral("/proc/self/mounts");
    }
    QFile f(file);
    if (!f.open(QIODevice::ReadOnly | QIODevice::Text)) {
        return {};
    }
    // /proc/mounts escreve espaço, tab, \n e \ como \ooo (octal)
    static const QRegularExpression octal(QStringLiteral("\\\\([0-7]{3})"));
    QStringList mounts;
    const QList<QByteArray> lines = f.readAll().split('\n');
    for (const QByteArray &line : lines) {
        const QList<QByteArray> parts = line.split(' ');
        if (parts.size() < 3 || parts.at(0) != "craftdriver" || !parts.at(2).startsWith("fuse")) {
            continue;
        }
        QString mp = QFile::decodeName(parts.at(1));
        QRegularExpressionMatch m;
        while ((m = octal.match(mp)).hasMatch()) {
            mp.replace(m.capturedStart(), m.capturedLength(), QChar(m.captured(1).toInt(nullptr, 8)));
        }
        mounts << mp;
    }
    return mounts;
}

bool insideMount(const QString &localPath, const QStringList &mounts)
{
    QString path = localPath;
    while (path.size() > 1 && path.endsWith(QLatin1Char('/'))) {
        path.chop(1);
    }
    for (const QString &mount : mounts) {
        QString root = mount;
        while (root.size() > 1 && root.endsWith(QLatin1Char('/'))) {
            root.chop(1);
        }
        if (path == root || path.startsWith(root == QLatin1String("/") ? root : root + QLatin1Char('/'))) {
            return true;
        }
    }
    return false;
}

bool insideMountResolved(const QString &localPath, const QStringList &mounts, QHash<QString, QString> *dirCache)
{
    if (mounts.isEmpty()) {
        return false;
    }
    const QString clean = QDir::cleanPath(localPath);
    if (insideMount(clean, mounts)) {
        return true;
    }
    // Caso a própria pasta (ou raiz da montagem) seja um symlink
    char selfBuf[PATH_MAX];
    if (::realpath(QFile::encodeName(clean).constData(), selfBuf)) {
        if (insideMount(QFile::decodeName(selfBuf), mounts)) {
            return true;
        }
    }
    const int slash = clean.lastIndexOf(QLatin1Char('/'));
    if (slash < 0) {
        return false;
    }
    const QString parent = slash == 0 ? QStringLiteral("/") : clean.left(slash);
    const QString name = clean.mid(slash + 1);
    QString real;
    if (dirCache && dirCache->contains(parent)) {
        real = dirCache->value(parent);
    } else {
        char buf[PATH_MAX];
        real = ::realpath(QFile::encodeName(parent).constData(), buf) ? QFile::decodeName(buf) : parent;
        if (dirCache) {
            if (dirCache->size() >= 512) {
                dirCache->clear();
            }
            dirCache->insert(parent, real);
        }
    }
    if (real == parent) {
        return false;
    }
    const QString resolved = real.endsWith(QLatin1Char('/')) ? QString(real + name) : QString(real + QLatin1Char('/') + name);
    return insideMount(resolved, mounts);
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
    //   emblem-important        "!" vermelho            → erro
    if (status.compare(QLatin1String("OnlineOnly"), Qt::CaseInsensitive) == 0 ||
        status.compare(QLatin1String("cloud-only"), Qt::CaseInsensitive) == 0) {
        return QStringLiteral("folder-cloud");
    }
    if (status.compare(QLatin1String("Available"), Qt::CaseInsensitive) == 0 ||
        status.compare(QLatin1String("synced"), Qt::CaseInsensitive) == 0) {
        return QStringLiteral("emblem-default-symbolic");
    }
    if (status.compare(QLatin1String("AlwaysAvailable"), Qt::CaseInsensitive) == 0 ||
        status.compare(QLatin1String("pinned"), Qt::CaseInsensitive) == 0) {
        return QStringLiteral("vcs-normal");
    }
    if (status.compare(QLatin1String("Syncing"), Qt::CaseInsensitive) == 0 ||
        status.compare(QLatin1String("syncing"), Qt::CaseInsensitive) == 0) {
        return QStringLiteral("vcs-update-required");
    }
    if (status.compare(QLatin1String("LocalOnly"), Qt::CaseInsensitive) == 0) {
        return QStringLiteral("emblem-unavailable");
    }
    if (status.compare(QLatin1String("Error"), Qt::CaseInsensitive) == 0 ||
        status.compare(QLatin1String("error"), Qt::CaseInsensitive) == 0) {
        return QStringLiteral("emblem-important");
    }
    return {}; // "Folder" e valores desconhecidos: sem emblema
}
}
