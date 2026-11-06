// Testa o plugin de emblemas carregando o .so como o Dolphin (QPluginLoader + qobject_cast).
#include "craftdriverstatus.h"
#include "testutil.h"

#include <KOverlayIconPlugin>
#include <KPluginMetaData>

#include <QDirIterator>
#include <QElapsedTimer>
#include <QIcon>
#include <QPluginLoader>
#include <QSignalSpy>
#include <QTest>

#include <cerrno>

class OverlayTest : public QObject
{
    Q_OBJECT
    std::unique_ptr<QTemporaryDir> m_tmp;
    QDir m_dir;
    KOverlayIconPlugin *m_plugin = nullptr;
    QHash<QString, QString> m_files; // status → caminho

    QUrl url(const QString &status) const
    {
        return QUrl::fromLocalFile(m_files.value(status));
    }

    // Espera overlaysChanged para a URL; devolve os emblemas do sinal (ou "<timeout>").
    QStringList waitFor(QSignalSpy &spy, const QUrl &u, int ms)
    {
        QElapsedTimer t;
        t.start();
        int seen = 0;
        while (t.elapsed() < ms) {
            for (; seen < spy.size(); ++seen) {
                if (spy.at(seen).at(0).toUrl() == u) {
                    return spy.at(seen).at(1).toStringList();
                }
            }
            spy.wait(50);
        }
        return {QStringLiteral("<timeout>")};
    }

private Q_SLOTS:
    void initTestCase()
    {
        // Intervalos curtos para o teste (antes do plugin ser instanciado).
        qputenv("CRAFTDRIVER_OVERLAY_SYNCING_MS", "200");
        qputenv("CRAFTDRIVER_OVERLAY_REFRESH_MS", "60000");
        qputenv("CRAFTDRIVER_OVERLAY_ACTIVE_MS", "3000");
        QIcon::setThemeName(QStringLiteral("breeze"));

        m_tmp = TestUtil::makeDir();
        QVERIFY(m_tmp->isValid());
        m_dir = QDir(m_tmp->path());
        const QString probe = TestUtil::touch(m_dir, QStringLiteral("probe"));
        if (!TestUtil::setStatus(probe, QStringLiteral("x"))) {
            QSKIP("o sistema de arquivos de $CRAFTDRIVER_TEST_DIR/$TMPDIR não aceita user xattrs");
        }
        for (const char *s : {"OnlineOnly", "Available", "AlwaysAvailable", "Syncing", "LocalOnly"}) {
            const QString st = QString::fromLatin1(s);
            const QString f = TestUtil::touch(m_dir, st + QStringLiteral(".txt"));
            QVERIFY(TestUtil::setStatus(f, st));
            m_files.insert(st, f);
        }
        QVERIFY(m_dir.mkdir(QStringLiteral("pasta")));
        QVERIFY(TestUtil::setStatus(m_dir.filePath(QStringLiteral("pasta")), QStringLiteral("Folder")));
        m_files.insert(QStringLiteral("Folder"), m_dir.filePath(QStringLiteral("pasta")));
        m_files.insert(QString(), TestUtil::touch(m_dir, QStringLiteral("fora.txt"))); // sem xattr
    }

    void emblemTable()
    {
        QCOMPARE(CraftDriver::emblemFor(QStringLiteral("OnlineOnly")), QStringLiteral("folder-cloud"));
        QCOMPARE(CraftDriver::emblemFor(QStringLiteral("Available")), QStringLiteral("emblem-default-symbolic"));
        QCOMPARE(CraftDriver::emblemFor(QStringLiteral("AlwaysAvailable")), QStringLiteral("vcs-normal"));
        QCOMPARE(CraftDriver::emblemFor(QStringLiteral("Syncing")), QStringLiteral("vcs-update-required"));
        QCOMPARE(CraftDriver::emblemFor(QStringLiteral("LocalOnly")), QStringLiteral("emblem-unavailable"));
        QCOMPARE(CraftDriver::emblemFor(QStringLiteral("Folder")), QString());
        QCOMPARE(CraftDriver::emblemFor(QStringLiteral("Inexistente")), QString());
        QCOMPARE(CraftDriver::emblemFor(QString()), QString());
    }

    // Os nomes escolhidos existem de verdade no Breeze (arquivo no tema, sem o fallback do Qt).
    void emblemsExistInBreeze()
    {
        const QDir breeze(QStringLiteral("/usr/share/icons/breeze"));
        if (!breeze.exists()) {
            QSKIP("tema Breeze não instalado");
        }
        for (const char *s : {"OnlineOnly", "Available", "AlwaysAvailable", "Syncing", "LocalOnly"}) {
            const QString name = CraftDriver::emblemFor(QString::fromLatin1(s)) + QStringLiteral(".svg");
            QDirIterator it(breeze.path(), {name}, QDir::Files, QDirIterator::Subdirectories);
            QVERIFY2(it.hasNext(), qPrintable(name));
        }
    }

    void readStatus()
    {
        int err = -1;
        QCOMPARE(CraftDriver::readStatus(m_files.value(QStringLiteral("Syncing")), &err), QStringLiteral("Syncing"));
        QCOMPARE(err, 0);
        QCOMPARE(CraftDriver::readStatus(m_files.value(QString()), &err), QString());
        QCOMPARE(err, ENODATA);
        QCOMPARE(CraftDriver::readStatus(m_dir.filePath(QStringLiteral("nao-existe")), &err), QString());
        QCOMPARE(err, ENOENT);
    }

    // Tabela de montagens: só fsname=craftdriver em fuse.*, com os escapes octais do /proc/mounts.
    void mountTable()
    {
        const QString table = m_tmp->filePath(QStringLiteral("mounts"));
        QFile f(table);
        QVERIFY(f.open(QIODevice::WriteOnly));
        f.write("/dev/sda1 / ext4 rw 0 0\n"
                "craftdriver /home/u/One\\040Drive fuse.onedrive rw,nosuid 0 0\n"
                "outrofs /home/u/remoto fuse.sshfs rw 0 0\n"
                "craftdriver /mnt/cd/ fuse.onedrive rw 0 0\n");
        f.close();
        qputenv("CRAFTDRIVER_MOUNTS_FILE", QFile::encodeName(table));
        const QStringList mounts = CraftDriver::craftdriverMounts();
        qunsetenv("CRAFTDRIVER_MOUNTS_FILE");
        QCOMPARE(mounts, (QStringList{QStringLiteral("/home/u/One Drive"), QStringLiteral("/mnt/cd/")}));
        QVERIFY(CraftDriver::insideMount(QStringLiteral("/home/u/One Drive"), mounts));
        QVERIFY(CraftDriver::insideMount(QStringLiteral("/home/u/One Drive/a/b.txt"), mounts));
        QVERIFY(CraftDriver::insideMount(QStringLiteral("/mnt/cd/x"), mounts));
        QVERIFY(!CraftDriver::insideMount(QStringLiteral("/home/u/One Drive2/x"), mounts));
        QVERIFY(!CraftDriver::insideMount(QStringLiteral("/home/u/remoto/x"), mounts));
    }

    // Mesmo caminho do KFileItemModelRolesUpdater do Dolphin 26.08.
    void loadsLikeDolphin()
    {
        const auto plugins = KPluginMetaData::findPlugins(QStringLiteral("kf6/overlayicon"), {}, KPluginMetaData::AllowEmptyMetaData);
        QString file;
        for (const KPluginMetaData &md : plugins) {
            if (md.pluginId() == QLatin1String("craftdriveroverlayplugin")) {
                file = md.fileName();
            }
        }
        QVERIFY2(!file.isEmpty(), "plugin não encontrado em kf6/overlayicon (QT_PLUGIN_PATH)");
        QPluginLoader loader(file);
        m_plugin = qobject_cast<KOverlayIconPlugin *>(loader.instance());
        QVERIFY2(m_plugin, qPrintable(loader.errorString()));
        QCOMPARE(qApp->property("craftdriverOverlayPlugin").value<QObject *>(), m_plugin);
    }

    void asyncFirstAnswer()
    {
        QVERIFY(m_plugin);
        QSignalSpy spy(m_plugin, &KOverlayIconPlugin::overlaysChanged);
        const QHash<QString, QString> expected{
            {QStringLiteral("OnlineOnly"), QStringLiteral("folder-cloud")},
            {QStringLiteral("Available"), QStringLiteral("emblem-default-symbolic")},
            {QStringLiteral("AlwaysAvailable"), QStringLiteral("vcs-normal")},
            {QStringLiteral("Syncing"), QStringLiteral("vcs-update-required")},
            {QStringLiteral("LocalOnly"), QStringLiteral("emblem-unavailable")},
        };
        // 1ª chamada: não bloqueia, devolve vazio e responde depois por sinal.
        for (auto it = m_files.cbegin(); it != m_files.cend(); ++it) {
            QCOMPARE(m_plugin->getOverlays(QUrl::fromLocalFile(it.value())), QStringList());
        }
        QCOMPARE(m_plugin->getOverlays(QUrl(QStringLiteral("https://example.com/x"))), QStringList());
        for (auto it = expected.cbegin(); it != expected.cend(); ++it) {
            QCOMPARE(waitFor(spy, url(it.key()), 3000), QStringList{it.value()});
            QCOMPARE(m_plugin->getOverlays(url(it.key())), QStringList{it.value()});
        }
        // Pasta comum e arquivo fora do craftDriver: nenhum emblema e nenhum sinal.
        QTest::qWait(300);
        for (const auto &args : std::as_const(spy)) {
            QVERIFY(args.at(0).toUrl() != url(QStringLiteral("Folder")));
            QVERIFY(args.at(0).toUrl() != url(QString()));
        }
        QCOMPARE(m_plugin->getOverlays(url(QStringLiteral("Folder"))), QStringList());
        QCOMPARE(m_plugin->getOverlays(url(QString())), QStringList());
        QCOMPARE(spy.size(), expected.size());
    }

    // "Syncing" é relido rápido: a mudança aparece sem o Dolphin pedir de novo.
    void syncingIsPolled()
    {
        QSignalSpy spy(m_plugin, &KOverlayIconPlugin::overlaysChanged);
        QVERIFY(TestUtil::setStatus(m_files.value(QStringLiteral("Syncing")), QStringLiteral("AlwaysAvailable")));
        QCOMPARE(waitFor(spy, url(QStringLiteral("Syncing")), 3000), QStringList{QStringLiteral("vcs-normal")});
        QCOMPARE(m_plugin->getOverlays(url(QStringLiteral("Syncing"))), QStringList{QStringLiteral("vcs-normal")});
    }

    // Itens estáveis só são relidos no intervalo longo (60 s aqui) — a não ser que o plugin de
    // ações chame refreshPaths() (o que ele faz depois de rodar o CLI).
    void refreshPathsHook()
    {
        QSignalSpy spy(m_plugin, &KOverlayIconPlugin::overlaysChanged);
        QVERIFY(TestUtil::setStatus(m_files.value(QStringLiteral("Available")), QStringLiteral("OnlineOnly")));
        QCOMPARE(waitFor(spy, url(QStringLiteral("Available")), 1000), QStringList{QStringLiteral("<timeout>")});

        QObject *hook = qApp->property("craftdriverOverlayPlugin").value<QObject *>();
        QVERIFY(QMetaObject::invokeMethod(hook, "refreshPaths", Qt::QueuedConnection, Q_ARG(QStringList, QStringList{m_dir.path()})));
        QCOMPARE(waitFor(spy, url(QStringLiteral("Available")), 1500), QStringList{QStringLiteral("folder-cloud")});
        // Depois do refreshPaths o item fica em releitura rápida por ~30 s.
        spy.clear();
        QVERIFY(TestUtil::setStatus(m_files.value(QStringLiteral("Available")), QStringLiteral("AlwaysAvailable")));
        QCOMPARE(waitFor(spy, url(QStringLiteral("Available")), 1500), QStringList{QStringLiteral("vcs-normal")});
    }

    void deletedFileClearsEmblem()
    {
        QSignalSpy spy(m_plugin, &KOverlayIconPlugin::overlaysChanged);
        const QString f = m_files.value(QStringLiteral("Available")); // em releitura rápida (teste anterior)
        m_plugin->getOverlays(QUrl::fromLocalFile(f)); // o Dolphin mostrando o item
        QVERIFY(QFile::remove(f));
        QCOMPARE(waitFor(spy, QUrl::fromLocalFile(f), 3000), QStringList());
    }

    // Entradas que o Dolphin não pede há mais de CRAFTDRIVER_OVERLAY_ACTIVE_MS (3 s aqui) não são relidas,
    // nem em releitura rápida ou "Syncing"; voltam a ser quando o Dolphin pede de novo.
    void staleEntriesAreNotReread()
    {
        QSignalSpy spy(m_plugin, &KOverlayIconPlugin::overlaysChanged);
        const QUrl u = url(QStringLiteral("LocalOnly"));
        QCOMPARE(m_plugin->getOverlays(u), QStringList{QStringLiteral("emblem-unavailable")});
        QObject *hook = qApp->property("craftdriverOverlayPlugin").value<QObject *>();
        QVERIFY(QMetaObject::invokeMethod(hook, "refreshPaths", Qt::QueuedConnection, Q_ARG(QStringList, QStringList{u.toLocalFile()})));
        QTest::qWait(3500); // passa da janela de uso sem o Dolphin pedir o item
        spy.clear();
        QVERIFY(TestUtil::setStatus(u.toLocalFile(), QStringLiteral("OnlineOnly")));
        QCOMPARE(waitFor(spy, u, 1000), QStringList{QStringLiteral("<timeout>")});
        // o Dolphin mostra o item de novo: ainda em releitura rápida, a mudança chega
        QCOMPARE(m_plugin->getOverlays(u), QStringList{QStringLiteral("emblem-unavailable")});
        QCOMPARE(waitFor(spy, u, 1500), QStringList{QStringLiteral("folder-cloud")});
        // "Syncing" fora de vista também para de ser relido
        const QUrl s = url(QStringLiteral("Syncing"));
        QVERIFY(TestUtil::setStatus(s.toLocalFile(), QStringLiteral("Syncing")));
        QVERIFY(QMetaObject::invokeMethod(hook, "refreshPaths", Qt::QueuedConnection, Q_ARG(QStringList, QStringList{s.toLocalFile()})));
        QCOMPARE(waitFor(spy, s, 1500), QStringList{QStringLiteral("vcs-update-required")});
        QTest::qWait(3500);
        spy.clear();
        QVERIFY(TestUtil::setStatus(s.toLocalFile(), QStringLiteral("Available")));
        QCOMPARE(waitFor(spy, s, 800), QStringList{QStringLiteral("<timeout>")});
        m_plugin->getOverlays(s);
        QCOMPARE(waitFor(spy, s, 1500), QStringList{QStringLiteral("emblem-default-symbolic")});
        // cachedStatus (usado pelo plugin de ações) devolve o último lido, sem ler
        QString st;
        QVERIFY(QMetaObject::invokeMethod(hook, "cachedStatus", Qt::DirectConnection, Q_RETURN_ARG(QString, st), Q_ARG(QString, s.toLocalFile())));
        QCOMPARE(st, QStringLiteral("Available"));
    }
};

QTEST_MAIN(OverlayTest)
#include "overlaytest.moc"
