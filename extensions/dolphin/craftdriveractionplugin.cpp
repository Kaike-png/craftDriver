// Menu "craftDriver" no Dolphin, mostrado só quando TODOS os itens selecionados estão numa montagem do
// craftDriver (fsname=craftdriver na tabela de montagens) — funciona com qualquer ponto de montagem.
//
// actions() roda na thread da interface: não lê xattr (um getxattr num FUSE lento travaria o Dolphin a cada
// clique com o direito). O status para habilitar "Sempre manter"/"Liberar espaço" vem do cache do plugin de
// emblemas (que lê numa thread própria); sem ele, tudo fica habilitado e o CLI responde.
#include "craftdriverstatus.h"

#include <KAbstractFileItemActionPlugin>
#include <KFileItem>
#include <KFileItemListProperties>
#include <KPluginFactory>

#include <QAction>
#include <QCoreApplication>
#include <QDir>
#include <QElapsedTimer>
#include <QIcon>
#include <QMenu>
#include <QProcess>
#include <QStandardPaths>
#include <QUrl>

#include <algorithm>

class CraftDriverActionPlugin : public KAbstractFileItemActionPlugin
{
    Q_OBJECT
public:
    CraftDriverActionPlugin(QObject *parent, const QVariantList &)
        : KAbstractFileItemActionPlugin(parent)
    {
    }

    QList<QAction *> actions(const KFileItemListProperties &props, QWidget *parentWidget) override
    {
        const KFileItemList items = props.items();
        if (items.isEmpty()) {
            return {};
        }
        const QStringList mounts = craftdriverMounts();
        QObject *overlay = qApp ? qApp->property("craftdriverOverlayPlugin").value<QObject *>() : nullptr;
        QStringList paths;
        QStringList statuses;
        bool singleFile = items.size() == 1;
        for (const KFileItem &item : items) {
            const QString path = item.localPath();
            if (path.isEmpty() || !CraftDriver::insideMountResolved(QDir::cleanPath(path), mounts, &m_canonical)) {
                return {}; // algum item fora do craftDriver: sem menu
            }
            // status já lido pelo plugin de emblemas (vazio = desconhecido)
            QString status;
            if (overlay) {
                QMetaObject::invokeMethod(overlay, "cachedStatus", Qt::DirectConnection, Q_RETURN_ARG(QString, status),
                                          Q_ARG(QString, QDir::cleanPath(path)));
            }
            if (status == QLatin1String("Folder") || (status.isEmpty() && item.isDir())) {
                singleFile = false;
            }
            paths << path;
            statuses << status;
        }

        QObject *owner = parentWidget ? static_cast<QObject *>(parentWidget) : this;
        auto *menu = new QMenu(parentWidget);
        menu->setTitle(QStringLiteral("craftDriver"));
        menu->setIcon(QIcon::fromTheme(QStringLiteral("craftdriver"), QIcon::fromTheme(QStringLiteral("folder-cloud"))));
        if (!parentWidget) {
            connect(this, &QObject::destroyed, menu, &QObject::deleteLater);
        }

        const bool allPinned = std::all_of(statuses.cbegin(), statuses.cend(), [](const QString &s) {
            return s == QLatin1String("AlwaysAvailable");
        });
        const bool allOnline = std::all_of(statuses.cbegin(), statuses.cend(), [](const QString &s) {
            return s == QLatin1String("OnlineOnly");
        });

        auto add = [&](const QString &name, const QString &text, const QString &icon, const QString &command, bool enabled, bool notify = true) {
            auto *a = new QAction(QIcon::fromTheme(icon), text, owner);
            a->setObjectName(name);
            a->setEnabled(enabled);
            connect(a, &QAction::triggered, this, [this, command, paths, notify]() {
                run(command, paths, notify);
            });
            menu->addAction(a);
        };
        // Mesmos textos/comandos do servicemenu packaging/share/kio/servicemenus/craftdriver.desktop.
        add(QStringLiteral("craftdriver_pin"), QStringLiteral("Sempre manter neste dispositivo"), QStringLiteral("emblem-downloads"), QStringLiteral("pin"), !allPinned);
        add(QStringLiteral("craftdriver_free"), QStringLiteral("Liberar espaço"), QStringLiteral("edit-clear"), QStringLiteral("unpin"), !allOnline);
        add(QStringLiteral("craftdriver_status"), QStringLiteral("Status no craftDriver"), QStringLiteral("help-about"), QStringLiteral("status"), true);
        menu->addSeparator();
        add(QStringLiteral("craftdriver_share"), QStringLiteral("Copiar link de compartilhamento"), QStringLiteral("insert-link"), QStringLiteral("share"), true);
        // Versões: um arquivo só (pastas não têm versões); abre a janela do app, sem notificação de sucesso
        add(QStringLiteral("craftdriver_versions"), QStringLiteral("Versões anteriores…"), QStringLiteral("document-open-recent"), QStringLiteral("versions"), singleFile, false);

        menu->menuAction()->setObjectName(QStringLiteral("craftdriver_menu"));
        return {menu->menuAction()};
    }

private:
    // Tabela de montagens relida no máximo a cada 2 s (vários menus seguidos não releem o procfs).
    QStringList craftdriverMounts()
    {
        if (!m_mountsAge.isValid() || m_mountsAge.elapsed() > 2000) {
            m_mounts = CraftDriver::craftdriverMounts();
            m_mountsAge.start();
        }
        return m_mounts;
    }

    QStringList m_mounts;
    QElapsedTimer m_mountsAge;
    QHash<QString, QString> m_canonical; // pasta -> realpath (montagem atrás de symlink)

    // Binário do CLI: $CRAFTDRIVER_BIN (testes), PATH, ou ~/.local/bin (instalação em modo usuário).
    static QString craftdriverBinary()
    {
        const QString env = qEnvironmentVariable("CRAFTDRIVER_BIN");
        if (!env.isEmpty()) {
            return env;
        }
        QString bin = QStandardPaths::findExecutable(QStringLiteral("craftdriver"));
        if (bin.isEmpty()) {
            bin = QStandardPaths::findExecutable(QStringLiteral("craftdriver"), {QDir::homePath() + QStringLiteral("/.local/bin")});
        }
        return bin;
    }

    void run(const QString &command, const QStringList &paths, bool notify)
    {
        const QString bin = craftdriverBinary();
        if (bin.isEmpty()) {
            Q_EMIT error(QStringLiteral("craftDriver: comando \"craftdriver\" não encontrado no PATH."));
            return;
        }
        QStringList args{command};
        if (notify) {
            args << QStringLiteral("--notify");
        }
        args << paths;
        if (!QProcess::startDetached(bin, args)) {
            Q_EMIT error(QStringLiteral("craftDriver: não foi possível executar %1.").arg(bin));
            return;
        }
        // Pede ao plugin de emblemas (se carregado neste processo) que releia esses itens logo.
        if (qApp) {
            if (QObject *overlay = qApp->property("craftdriverOverlayPlugin").value<QObject *>()) {
                QMetaObject::invokeMethod(overlay, "refreshPaths", Qt::QueuedConnection, Q_ARG(QStringList, paths));
            }
        }
    }
};

K_PLUGIN_CLASS_WITH_JSON(CraftDriverActionPlugin, "craftdriveractionplugin.json")

#include "craftdriveractionplugin.moc"
