// Ferramenta de diagnóstico (não é instalada): carrega o plugin de emblemas como o Dolphin e mostra
// status → emblema de cada caminho. Uso: QT_PLUGIN_PATH=<build>/bin craftdriver-overlay-probe <arquivos...>
#include "craftdriverstatus.h"

#include <KOverlayIconPlugin>
#include <KPluginMetaData>

#include <QGuiApplication>
#include <QPluginLoader>
#include <QTimer>
#include <QUrl>

#include <cstdio>

int main(int argc, char **argv)
{
    QGuiApplication app(argc, argv);
    const QStringList paths = app.arguments().mid(1);
    KOverlayIconPlugin *plugin = nullptr;
    const auto plugins = KPluginMetaData::findPlugins(QStringLiteral("kf6/overlayicon"), {}, KPluginMetaData::AllowEmptyMetaData);
    for (const KPluginMetaData &md : plugins) {
        if (md.pluginId() == QLatin1String("craftdriveroverlayplugin")) {
            plugin = qobject_cast<KOverlayIconPlugin *>(QPluginLoader(md.fileName()).instance());
        }
    }
    if (!plugin) {
        std::fprintf(stderr, "plugin craftdriveroverlayplugin não encontrado (QT_PLUGIN_PATH)\n");
        return 2;
    }
    for (const QString &p : paths) {
        plugin->getOverlays(QUrl::fromLocalFile(p)); // 1ª chamada só agenda a leitura
    }
    QTimer::singleShot(1500, &app, [&]() {
        for (const QString &p : paths) {
            std::printf("%-20s %-28s %s\n", qPrintable(CraftDriver::readStatus(p)),
                        qPrintable(plugin->getOverlays(QUrl::fromLocalFile(p)).join(QLatin1Char(','))), qPrintable(p));
        }
        app.quit();
    });
    return app.exec();
}
