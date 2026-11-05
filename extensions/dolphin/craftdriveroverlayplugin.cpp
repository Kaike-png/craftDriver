// Emblemas do craftDriver no Dolphin.
//
// O Dolphin chama getOverlays() na thread principal e pede que ela não bloqueie: o getxattr num
// FUSE passa pelo processo do craftDriver, então a leitura roda numa thread própria. getOverlays()
// devolve o que está em cache (ou nada) e, quando o valor chega/muda, emitimos overlaysChanged().
//
// Não há notificação de mudança de status vinda do FUSE, então itens já vistos são relidos
// periodicamente (os "Syncing" com mais frequência). O plugin de ações chama refreshPaths() depois
// de rodar o CLI para o emblema mudar logo (via propriedade do qApp, sem dependência de link).
#include "craftdriverstatus.h"

#include <KOverlayIconPlugin>

#include <QCoreApplication>
#include <QDateTime>
#include <QDir>
#include <QHash>
#include <QPointer>
#include <QThread>
#include <QTimer>
#include <QUrl>

#include <algorithm>
#include <cerrno>

namespace
{
int envInt(const char *name, int fallback)
{
    bool ok = false;
    const int v = qEnvironmentVariableIntValue(name, &ok);
    return ok && v > 0 ? v : fallback;
}
}

// Roda na thread de leitura: só faz syscalls e devolve os resultados por sinal (fila).
class CraftDriverStatusWorker : public QObject
{
    Q_OBJECT
public Q_SLOTS:
    void query(const QStringList &paths)
    {
        for (const QString &path : paths) {
            int err = 0;
            const QString status = CraftDriver::readStatus(path, &err);
            Q_EMIT result(path, status, err == ENOENT);
        }
    }
Q_SIGNALS:
    void result(const QString &path, const QString &status, bool gone);
};

class CraftDriverOverlayPlugin : public KOverlayIconPlugin
{
    Q_OBJECT
    Q_PLUGIN_METADATA(IID "org.kde.overlayicon.craftdriver" FILE "craftdriveroverlayplugin.json")

public:
    explicit CraftDriverOverlayPlugin(QObject *parent = nullptr)
        : KOverlayIconPlugin(parent)
        , m_syncingMs(envInt("CRAFTDRIVER_OVERLAY_SYNCING_MS", 2000))
        , m_idleMs(envInt("CRAFTDRIVER_OVERLAY_REFRESH_MS", 10000))
        , m_maxEntries(envInt("CRAFTDRIVER_OVERLAY_MAX_ENTRIES", 4000))
    {
        m_thread.setObjectName(QStringLiteral("craftdriver-overlay"));
        m_worker = new CraftDriverStatusWorker;
        m_worker->moveToThread(&m_thread);
        connect(&m_thread, &QThread::finished, m_worker, &QObject::deleteLater);
        connect(m_worker, &CraftDriverStatusWorker::result, this, &CraftDriverOverlayPlugin::onResult, Qt::QueuedConnection);
        m_thread.start();

        m_timer.setInterval(qMin(m_syncingMs, m_idleMs) / 2 + 1);
        connect(&m_timer, &QTimer::timeout, this, &CraftDriverOverlayPlugin::refreshDue);
        m_timer.start();

        // Ponto de contato para o plugin de ações (ver refreshPaths).
        if (qApp) {
            qApp->setProperty("craftdriverOverlayPlugin", QVariant::fromValue<QObject *>(this));
        }
    }

    ~CraftDriverOverlayPlugin() override
    {
        if (qApp && qApp->property("craftdriverOverlayPlugin").value<QObject *>() == this) {
            qApp->setProperty("craftdriverOverlayPlugin", QVariant());
        }
        m_thread.quit();
        // Um FUSE travado prenderia a thread num getxattr; não segura o encerramento do Dolphin por isso.
        if (!m_thread.wait(2000)) {
            qWarning("craftdriveroverlayplugin: thread de leitura não terminou (FUSE travado?)");
        }
    }

    QStringList getOverlays(const QUrl &url) override
    {
        if (!url.isLocalFile()) {
            return {};
        }
        const QString path = QDir::cleanPath(url.toLocalFile());
        const qint64 now = QDateTime::currentMSecsSinceEpoch();
        auto it = m_cache.find(path);
        if (it == m_cache.end()) {
            Entry e;
            e.lastUsed = now;
            e.pending = true;
            m_cache.insert(path, e);
            evictIfNeeded();
            request({path});
            return {};
        }
        it->lastUsed = now;
        return overlaysFor(it->status);
    }

public Q_SLOTS:
    // Releitura imediata (e acelerada por ~30 s) dos caminhos e de tudo em cache abaixo deles.
    // Chamado pelo plugin de ações via QMetaObject::invokeMethod depois de fixar/liberar.
    void refreshPaths(const QStringList &paths)
    {
        const qint64 until = QDateTime::currentMSecsSinceEpoch() + 30000;
        QStringList now;
        for (auto it = m_cache.begin(); it != m_cache.end(); ++it) {
            for (const QString &p : paths) {
                const QString clean = QDir::cleanPath(p);
                if (it.key() == clean || it.key().startsWith(clean + QLatin1Char('/'))) {
                    it->fastUntil = until;
                    if (!it->pending) {
                        it->pending = true;
                        now << it.key();
                    }
                    break;
                }
            }
        }
        request(now);
    }

private Q_SLOTS:
    void onResult(const QString &path, const QString &status, bool gone)
    {
        auto it = m_cache.find(path);
        if (it == m_cache.end()) {
            return; // despejado do cache enquanto lia
        }
        // Na primeira resposta o status ainda é "" e getOverlays() devolveu "nada", então
        // "before" é exatamente o que o Dolphin está mostrando.
        const QStringList before = overlaysFor(it->status);
        const QStringList after = overlaysFor(status);
        if (gone) {
            m_cache.erase(it);
        } else {
            it->pending = false;
            it->lastChecked = QDateTime::currentMSecsSinceEpoch();
            it->status = status;
        }
        if (before != after) {
            Q_EMIT overlaysChanged(QUrl::fromLocalFile(path), after);
        }
    }

    void refreshDue()
    {
        const qint64 now = QDateTime::currentMSecsSinceEpoch();
        QStringList due;
        for (auto it = m_cache.begin(); it != m_cache.end(); ++it) {
            Entry &e = it.value();
            if (e.pending || e.status.isEmpty()) {
                continue; // fora do craftDriver: não relê
            }
            const bool fast = e.status == QLatin1String("Syncing") || e.fastUntil > now;
            if (now - e.lastChecked >= (fast ? m_syncingMs : m_idleMs)) {
                e.pending = true;
                due << it.key();
            }
        }
        request(due);
    }

private:
    struct Entry {
        QString status;
        qint64 lastUsed = 0;
        qint64 lastChecked = 0;
        qint64 fastUntil = 0;
        bool pending = false;
    };

    static QStringList overlaysFor(const QString &status)
    {
        const QString icon = CraftDriver::emblemFor(status);
        return icon.isEmpty() ? QStringList() : QStringList{icon};
    }

    void request(const QStringList &paths)
    {
        if (!paths.isEmpty()) {
            QMetaObject::invokeMethod(m_worker, "query", Qt::QueuedConnection, Q_ARG(QStringList, paths));
        }
    }

    // Mantém o cache limitado: remove os itens que o Dolphin não pede há mais tempo.
    void evictIfNeeded()
    {
        if (m_cache.size() <= m_maxEntries) {
            return;
        }
        QList<qint64> stamps;
        stamps.reserve(m_cache.size());
        for (const Entry &e : std::as_const(m_cache)) {
            stamps << e.lastUsed;
        }
        std::sort(stamps.begin(), stamps.end());
        const qint64 cutoff = stamps.at(stamps.size() - m_maxEntries * 3 / 4);
        for (auto it = m_cache.begin(); it != m_cache.end();) {
            it = it->lastUsed < cutoff ? m_cache.erase(it) : std::next(it);
        }
    }

    QThread m_thread;
    QPointer<CraftDriverStatusWorker> m_worker;
    QTimer m_timer;
    QHash<QString, Entry> m_cache;
    const int m_syncingMs;
    const int m_idleMs;
    const int m_maxEntries;
};

#include "craftdriveroverlayplugin.moc"
