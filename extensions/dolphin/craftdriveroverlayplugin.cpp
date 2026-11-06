// Emblemas do craftDriver no Dolphin.
//
// O Dolphin chama getOverlays() na thread principal e pede que ela não bloqueie: o getxattr num
// FUSE passa pelo processo do craftDriver, então a leitura roda numa thread própria. getOverlays()
// devolve o que está em cache (ou nada) e, quando o valor chega/muda, emitimos overlaysChanged().
//
// Não há notificação de mudança de status vinda do FUSE, então itens já vistos são relidos
// periodicamente (os "Syncing" com mais frequência) — mas só os que o Dolphin pediu recentemente
// (CRAFTDRIVER_OVERLAY_ACTIVE_MS, padrão 2 min): antes todo o cache (até 4000 entradas) era relido a cada
// 10 s enquanto o Dolphin vivesse, ≈ 400 getxattr/s mesmo com as pastas fora de vista. O Dolphin chama
// getOverlays() de novo a cada overlaysChanged e ao mostrar a pasta, o que renova a entrada. O plugin de ações chama refreshPaths() depois
// de rodar o CLI para o emblema mudar logo (via propriedade do qApp, sem dependência de link).
//
// Só itens dentro de uma montagem do craftDriver (tabela de montagens relida a cada 2 s, como no plugin de ações)
// vão para a fila: antes todo arquivo de todo disco gerava um getxattr. Entrada com resposta vazia (ex.: vista
// enquanto o craftDriver ainda montava) é relida no intervalo longo e quando o Dolphin pede de novo.
#include "craftdriverstatus.h"

#include <KOverlayIconPlugin>

#include <QCoreApplication>
#include <QDateTime>
#include <QDir>
#include <QElapsedTimer>
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
            if (QThread::currentThread()->isInterruptionRequested()) {
                return; // plugin destruído (ver ~CraftDriverOverlayPlugin)
            }
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
        , m_activeMs(envInt("CRAFTDRIVER_OVERLAY_ACTIVE_MS", 120000))
    {
        // Sem dono: se no fim a thread estiver presa num getxattr, ela é vazada (ver o destrutor).
        m_thread = new QThread;
        m_thread->setObjectName(QStringLiteral("craftdriver-overlay"));
        m_worker = new CraftDriverStatusWorker;
        m_worker->moveToThread(m_thread);
        connect(m_thread, &QThread::finished, m_worker, &QObject::deleteLater);
        connect(m_worker, &CraftDriverStatusWorker::result, this, &CraftDriverOverlayPlugin::onResult, Qt::QueuedConnection);
        m_thread->start();

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
        m_thread->requestInterruption();
        m_thread->quit();
        // Um FUSE travado prenderia a thread num getxattr; não segura o encerramento do Dolphin por isso. Destruir um
        // QThread ainda rodando é qFatal (o Dolphin abortaria ao fechar): nesse caso a thread é vazada de propósito —
        // termina sozinha quando o getxattr voltar (o worker vê a interrupção e o finished apaga o worker) ou morre
        // com o processo. Os resultados não chegam mais aqui: a conexão com este objeto some no ~QObject.
        if (m_thread->wait(2000)) {
            delete m_thread;
        } else {
            qWarning("craftdriveroverlayplugin: thread de leitura não terminou (FUSE travado?); deixada para trás");
        }
    }

    QStringList getOverlays(const QUrl &url) override
    {
        if (!url.isLocalFile()) {
            return {};
        }
        const QString path = QDir::cleanPath(url.toLocalFile());
        if (!CraftDriver::insideMountResolved(path, mounts(), &m_canonical)) {
            return {};
        }
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
        // resposta vazia (sem xattr ainda, ex.: o craftDriver montava): o Dolphin pediu de novo, relê (com limite)
        if (it->status.isEmpty() && !it->pending && now - it->lastChecked >= m_syncingMs) {
            it->pending = true;
            request({path});
        }
        return overlaysFor(it->status);
    }

    // Último status lido (vazio = desconhecido ou fora do craftDriver), sem ler nada. Usado pelo plugin de ações
    // (QMetaObject::invokeMethod, mesma thread) para habilitar as ações sem getxattr na thread da interface.
    Q_INVOKABLE QString cachedStatus(const QString &path) const
    {
        const auto it = m_cache.constFind(QDir::cleanPath(path));
        return it == m_cache.cend() ? QString() : it->status;
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
                    it->lastUsed = QDateTime::currentMSecsSinceEpoch(); // o usuário acabou de agir nesses itens
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
            if (e.pending) {
                continue;
            }
            const bool syncing = e.status == QLatin1String("Syncing");
            if (now - e.lastUsed > m_activeMs) {
                continue; // fora de vista há muito tempo: relê quando o Dolphin pedir de novo
            }
            const bool fast = syncing || e.fastUntil > now;
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

    // Tabela de montagens relida no máximo a cada 2 s (lê o procfs, nunca o FUSE).
    QStringList mounts()
    {
        if (!m_mountsAge.isValid() || m_mountsAge.elapsed() > 2000) {
            m_mounts = CraftDriver::craftdriverMounts();
            m_mountsAge.start();
        }
        return m_mounts;
    }

    QThread *m_thread = nullptr;
    QPointer<CraftDriverStatusWorker> m_worker;
    QStringList m_mounts;
    QElapsedTimer m_mountsAge;
    QHash<QString, QString> m_canonical; // pasta -> realpath (montagem atrás de symlink)
    QTimer m_timer;
    QHash<QString, Entry> m_cache;
    const int m_syncingMs;
    const int m_idleMs;
    const int m_maxEntries;
    const int m_activeMs;
};

#include "craftdriveroverlayplugin.moc"
