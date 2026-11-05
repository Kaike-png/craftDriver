// Testa o plugin de ações: filtro por xattr, estado das ações, execução do CLI e a integração com
// o KFileItemActions real (o mesmo que monta o menu de contexto do Dolphin), incluindo o
// kservicemenurc que esconde o servicemenu genérico.
#include "craftdriverstatus.h"
#include "testutil.h"

#include <KAbstractFileItemActionPlugin>
#include <KFileItemActions>
#include <KFileItemListProperties>
#include <KPluginFactory>
#include <KPluginMetaData>

#include <QAction>
#include <QMenu>
#include <QSignalSpy>
#include <QTest>

class ActionsTest : public QObject
{
    Q_OBJECT
    std::unique_ptr<QTemporaryDir> m_tmp;
    QDir m_dir;
    KAbstractFileItemActionPlugin *m_plugin = nullptr;
    QString m_online, m_pinned, m_avail, m_folder, m_outside;

    QList<QAction *> actionsFor(const QStringList &paths)
    {
        if (!m_plugin) {
            return {};
        }
        KFileItemList items;
        for (const QString &p : paths) {
            items << KFileItem(QUrl::fromLocalFile(p));
        }
        return m_plugin->actions(KFileItemListProperties(items), nullptr);
    }

    static QAction *find(const QList<QAction *> &top, const QString &name)
    {
        for (QAction *a : top) {
            if (a->objectName() == name) {
                return a;
            }
            if (a->menu()) {
                if (QAction *sub = find(a->menu()->actions(), name)) {
                    return sub;
                }
            }
        }
        return nullptr;
    }

    // Todas as ações com o texto, procurando em submenus.
    static int countText(const QList<QAction *> &top, const QString &text)
    {
        int n = 0;
        for (QAction *a : top) {
            if (a->text().remove(QLatin1Char('&')) == text) {
                ++n;
            }
            if (a->menu()) {
                n += countText(a->menu()->actions(), text);
            }
        }
        return n;
    }

private Q_SLOTS:
    void initTestCase()
    {
        m_tmp = TestUtil::makeDir();
        QVERIFY(m_tmp->isValid());
        m_dir = QDir(m_tmp->path());
        m_online = TestUtil::touch(m_dir, QStringLiteral("online.txt"));
        if (!TestUtil::setStatus(m_online, QStringLiteral("OnlineOnly"))) {
            QSKIP("o sistema de arquivos de $CRAFTDRIVER_TEST_DIR/$TMPDIR não aceita user xattrs");
        }
        m_pinned = TestUtil::touch(m_dir, QStringLiteral("fixado.pdf"), "%PDF-1.4\n");
        QVERIFY(TestUtil::setStatus(m_pinned, QStringLiteral("AlwaysAvailable")));
        m_avail = TestUtil::touch(m_dir, QStringLiteral("dados.bin"), QByteArray("\x00\x01\x02", 3));
        QVERIFY(TestUtil::setStatus(m_avail, QStringLiteral("Available")));
        QVERIFY(m_dir.mkdir(QStringLiteral("pasta")));
        m_folder = m_dir.filePath(QStringLiteral("pasta"));
        QVERIFY(TestUtil::setStatus(m_folder, QStringLiteral("Folder")));
        m_outside = TestUtil::touch(m_dir, QStringLiteral("fora.txt"));
    }

    void metadataAndLoad()
    {
        const auto plugins = KPluginMetaData::findPlugins(QStringLiteral("kf6/kfileitemaction"));
        KPluginMetaData md;
        for (const KPluginMetaData &p : plugins) {
            if (p.pluginId() == QLatin1String("craftdriveractionplugin")) {
                md = p;
            }
        }
        QVERIFY2(md.isValid(), "plugin não encontrado em kf6/kfileitemaction (QT_PLUGIN_PATH)");
        auto result = KPluginFactory::instantiatePlugin<KAbstractFileItemActionPlugin>(md, this);
        QVERIFY2(result.plugin, qPrintable(result.errorText));
        m_plugin = result.plugin;
        // O KFileItemActions só oferece o plugin para esses tipos (arquivos e pastas).
        QCOMPARE(md.mimeTypes(), (QStringList{QStringLiteral("application/octet-stream"), QStringLiteral("inode/directory")}));
    }

    void onlyForCraftDriverItems()
    {
        QVERIFY(m_plugin);
        QCOMPARE(actionsFor({m_outside}).size(), 0);
        QCOMPARE(actionsFor({m_online, m_outside}).size(), 0); // seleção mista: nada
        QCOMPARE(actionsFor({m_dir.filePath(QStringLiteral("nao-existe"))}).size(), 0);

        const auto acts = actionsFor({m_online});
        QCOMPARE(acts.size(), 1);
        QCOMPARE(acts.first()->objectName(), QStringLiteral("craftdriver_menu"));
        QVERIFY(acts.first()->menu());
        QCOMPARE(acts.first()->menu()->title(), QStringLiteral("craftDriver"));
        // 3 de sincronização + separador + link + versões
        QCOMPARE(acts.first()->menu()->actions().size(), 6);

        QCOMPARE(actionsFor({m_folder}).size(), 1);
        QCOMPARE(actionsFor({m_online, m_pinned, m_avail, m_folder}).size(), 1);
    }

    void enabledState()
    {
        QVERIFY(m_plugin);
        auto acts = actionsFor({m_pinned}); // já fixado: "Sempre manter" desabilitado
        QVERIFY(!find(acts, QStringLiteral("craftdriver_pin"))->isEnabled());
        QVERIFY(find(acts, QStringLiteral("craftdriver_free"))->isEnabled());
        acts = actionsFor({m_online}); // somente online: nada a liberar
        QVERIFY(find(acts, QStringLiteral("craftdriver_pin"))->isEnabled());
        QVERIFY(!find(acts, QStringLiteral("craftdriver_free"))->isEnabled());
        acts = actionsFor({m_online, m_pinned});
        QVERIFY(find(acts, QStringLiteral("craftdriver_pin"))->isEnabled());
        QVERIFY(find(acts, QStringLiteral("craftdriver_free"))->isEnabled());
        QVERIFY(find(acts, QStringLiteral("craftdriver_status"))->isEnabled());
        QVERIFY(find(acts, QStringLiteral("craftdriver_share"))->isEnabled());
        // versões: só com um arquivo selecionado
        QVERIFY(!find(acts, QStringLiteral("craftdriver_versions"))->isEnabled());
        QVERIFY(find(actionsFor({m_online}), QStringLiteral("craftdriver_versions"))->isEnabled());
        QVERIFY(!find(actionsFor({m_folder}), QStringLiteral("craftdriver_versions"))->isEnabled());
        QVERIFY(find(actionsFor({m_folder}), QStringLiteral("craftdriver_share"))->isEnabled());
    }

    void runsCli_data()
    {
        QTest::addColumn<QString>("action");
        QTest::addColumn<QString>("command");
        QTest::addColumn<bool>("single");
        QTest::addColumn<bool>("notify");
        QTest::newRow("pin") << QStringLiteral("craftdriver_pin") << QStringLiteral("pin") << false << true;
        QTest::newRow("free") << QStringLiteral("craftdriver_free") << QStringLiteral("unpin") << false << true;
        QTest::newRow("status") << QStringLiteral("craftdriver_status") << QStringLiteral("status") << false << true;
        QTest::newRow("share") << QStringLiteral("craftdriver_share") << QStringLiteral("share") << false << true;
        QTest::newRow("versions") << QStringLiteral("craftdriver_versions") << QStringLiteral("versions") << true << false;
    }

    void runsCli()
    {
        QVERIFY(m_plugin);
        QFETCH(QString, action);
        QFETCH(QString, command);
        QFETCH(bool, single);
        QFETCH(bool, notify);
        // CLI falso: grava um argumento por linha.
        const QString log = m_tmp->filePath(QStringLiteral("cli-") + command + QStringLiteral(".log"));
        const QString fake = m_tmp->filePath(QStringLiteral("fake-craftdriver"));
        QFile f(fake);
        QVERIFY(f.open(QIODevice::WriteOnly));
        f.write("#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$CRAFTDRIVER_TEST_LOG\"\n");
        f.close();
        QVERIFY(f.setPermissions(QFile::ReadOwner | QFile::WriteOwner | QFile::ExeOwner));
        qputenv("CRAFTDRIVER_BIN", fake.toUtf8());
        qputenv("CRAFTDRIVER_TEST_LOG", log.toUtf8());

        const QStringList selected = single ? QStringList{m_online} : QStringList{m_online, m_pinned}; // os dois habilitam tudo
        const auto acts = actionsFor(selected);
        QAction *a = find(acts, action);
        QVERIFY(a && a->isEnabled());
        a->trigger();
        QTRY_VERIFY_WITH_TIMEOUT(QFile::exists(log) && QFileInfo(log).size() > 0, 5000);
        QFile l(log);
        QVERIFY(l.open(QIODevice::ReadOnly));
        QStringList expectedArgs{command};
        if (notify) {
            expectedArgs << QStringLiteral("--notify");
        }
        expectedArgs << selected;
        QCOMPARE(QString::fromUtf8(l.readAll()).split(QLatin1Char('\n'), Qt::SkipEmptyParts), expectedArgs);
        qunsetenv("CRAFTDRIVER_BIN");
    }

    void missingCliEmitsError()
    {
        QVERIFY(m_plugin);
        qputenv("CRAFTDRIVER_BIN", m_tmp->filePath(QStringLiteral("nao-existe")).toUtf8());
        QSignalSpy spy(m_plugin, &KAbstractFileItemActionPlugin::error);
        find(actionsFor({m_online}), QStringLiteral("craftdriver_status"))->trigger();
        QCOMPARE(spy.size(), 1);
        QVERIFY(spy.at(0).at(0).toString().contains(QStringLiteral("craftDriver")));
        qunsetenv("CRAFTDRIVER_BIN");
    }

    // Menu montado pelo KFileItemActions (como no Dolphin) com o servicemenu genérico instalado
    // em XDG_DATA_HOME. Com o kservicemenurc do plugin em XDG_CONFIG_DIRS o fallback some e sobra só
    // o menu do plugin (e só em arquivos do craftDriver); sem ele, o menu aparece duplicado.
    void contextMenuIntegration_data()
    {
        QTest::addColumn<bool>("hideFallback");
        QTest::addColumn<QString>("selection");
        QTest::addColumn<int>("expected");
        QTest::addColumn<bool>("pluginShown");
        QTest::newRow("com kservicemenurc, texto") << true << QStringLiteral("online") << 1 << true;
        QTest::newRow("com kservicemenurc, pdf") << true << QStringLiteral("pinned") << 1 << true;
        QTest::newRow("com kservicemenurc, binário") << true << QStringLiteral("avail") << 1 << true;
        QTest::newRow("com kservicemenurc, pasta") << true << QStringLiteral("folder") << 1 << true;
        QTest::newRow("com kservicemenurc, vários arquivos") << true << QStringLiteral("online,pinned,avail") << 1 << true;
        QTest::newRow("com kservicemenurc, arquivo+pasta") << true << QStringLiteral("online,folder") << 1 << true;
        QTest::newRow("com kservicemenurc, dentro+fora") << true << QStringLiteral("online,outside") << 0 << false;
        QTest::newRow("com kservicemenurc, fora") << true << QStringLiteral("outside") << 0 << false;
        QTest::newRow("sem kservicemenurc, dentro (duplicado)") << false << QStringLiteral("online") << 2 << true;
        QTest::newRow("sem kservicemenurc, fora (fallback aparece em tudo)") << false << QStringLiteral("outside") << 1 << false;
    }

    void contextMenuIntegration()
    {
        QFETCH(bool, hideFallback);
        QFETCH(QString, selection);
        QFETCH(int, expected);
        QFETCH(bool, pluginShown);
        if (!QFile::exists(QStringLiteral(SERVICEMENU_FILE))) {
            QSKIP("servicemenu do repositório não encontrado: " SERVICEMENU_FILE);
        }
        QTemporaryDir xdg(m_tmp->filePath(QStringLiteral("xdg-XXXXXX")));
        QDir root(xdg.path());
        QVERIFY(root.mkpath(QStringLiteral("data/kio/servicemenus")) && root.mkpath(QStringLiteral("config")) && root.mkpath(QStringLiteral("etc")));
        const QString menu = root.filePath(QStringLiteral("data/kio/servicemenus/craftdriver.desktop"));
        QVERIFY(QFile::copy(QStringLiteral(SERVICEMENU_FILE), menu));
        QFile(menu).setPermissions(QFile::ReadOwner | QFile::WriteOwner | QFile::ExeOwner);
        if (hideFallback) {
            QVERIFY(QFile::copy(QStringLiteral(KSERVICEMENURC_FILE), root.filePath(QStringLiteral("etc/kservicemenurc"))));
        }
        qputenv("XDG_DATA_HOME", root.filePath(QStringLiteral("data")).toUtf8());
        qputenv("XDG_CONFIG_HOME", root.filePath(QStringLiteral("config")).toUtf8());
        qputenv("XDG_CONFIG_DIRS", root.filePath(QStringLiteral("etc")).toUtf8());

        const QHash<QString, QString> files{{QStringLiteral("online"), m_online},
                                            {QStringLiteral("pinned"), m_pinned},
                                            {QStringLiteral("avail"), m_avail},
                                            {QStringLiteral("folder"), m_folder},
                                            {QStringLiteral("outside"), m_outside}};
        KFileItemList items;
        const QStringList keys = selection.split(QLatin1Char(','));
        for (const QString &k : keys) {
            items << KFileItem(QUrl::fromLocalFile(files.value(k)));
        }
        KFileItemActions fia;
        fia.setItemListProperties(KFileItemListProperties(items));
        QMenu menuWidget;
        fia.addActionsTo(&menuWidget);
        QCOMPARE(countText(menuWidget.actions(), QStringLiteral("Sempre manter neste dispositivo")), expected);
        // as ações novas também têm de sumir do fallback (chaves no kservicemenurc)
        QCOMPARE(countText(menuWidget.actions(), QStringLiteral("Copiar link de compartilhamento")), expected);
        QCOMPARE(countText(menuWidget.actions(), QStringLiteral("Versões anteriores…")), expected);
        QCOMPARE(find(menuWidget.actions(), QStringLiteral("craftdriver_pin")) != nullptr, pluginShown);
    }
};

QTEST_MAIN(ActionsTest)
#include "actionstest.moc"
