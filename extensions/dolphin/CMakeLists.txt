# Plugins do Dolphin (KF6) para o craftDriver:
#   craftdriveroverlayplugin  (kf6/overlayicon)     emblemas a partir do xattr user.onedrive.status
#   craftdriveractionplugin   (kf6/kfileitemaction) menu "craftDriver" só para arquivos do craftDriver
#   kservicemenurc            (/etc/xdg)            esconde o servicemenu genérico (fallback) quando o plugin está instalado
#
# Compilar:  cmake -S extensions/dolphin -B build -DCMAKE_INSTALL_PREFIX=/usr && cmake --build build
# Testar:    ctest --test-dir build --output-on-failure
# Instalar:  sudo cmake --install build
cmake_minimum_required(VERSION 3.22)
project(craftdriver-dolphin VERSION 0.1.0 LANGUAGES CXX)

set(QT_MIN_VERSION "6.5.0")
set(KF_MIN_VERSION "6.0.0")

find_package(ECM ${KF_MIN_VERSION} REQUIRED NO_MODULE)
set(CMAKE_MODULE_PATH ${ECM_MODULE_PATH})

include(KDEInstallDirs)
include(KDECMakeSettings)
include(KDECompilerSettings NO_POLICY_SCOPE)
include(FeatureSummary)

find_package(Qt6 ${QT_MIN_VERSION} REQUIRED COMPONENTS Core Gui Widgets)
find_package(KF6 ${KF_MIN_VERSION} REQUIRED COMPONENTS CoreAddons KIO)

option(CRAFTDRIVER_HIDE_SERVICEMENU
       "Instala ${KDE_INSTALL_CONFDIR}/kservicemenurc escondendo o servicemenu genérico do craftDriver (o plugin o substitui)" ON)

# Código comum (leitura do xattr e tabela status → emblema), usado pelos dois plugins e pelos testes.
add_library(craftdriverstatus STATIC craftdriverstatus.cpp)
set_target_properties(craftdriverstatus PROPERTIES POSITION_INDEPENDENT_CODE ON)
target_link_libraries(craftdriverstatus PUBLIC Qt6::Core)
target_include_directories(craftdriverstatus PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})

# O Dolphin carrega os plugins de overlay com QPluginLoader::instance() + qobject_cast<KOverlayIconPlugin*>,
# então a classe exportada é o próprio plugin (Q_PLUGIN_METADATA), não uma KPluginFactory.
kcoreaddons_add_plugin(craftdriveroverlayplugin
    SOURCES craftdriveroverlayplugin.cpp
    INSTALL_NAMESPACE "kf6/overlayicon")
target_link_libraries(craftdriveroverlayplugin PRIVATE craftdriverstatus KF6::KIOCore Qt6::Gui)

kcoreaddons_add_plugin(craftdriveractionplugin
    SOURCES craftdriveractionplugin.cpp
    INSTALL_NAMESPACE "kf6/kfileitemaction")
target_link_libraries(craftdriveractionplugin PRIVATE craftdriverstatus KF6::KIOWidgets KF6::KIOCore KF6::CoreAddons Qt6::Widgets)

if(CRAFTDRIVER_HIDE_SERVICEMENU)
    install(FILES kservicemenurc DESTINATION ${KDE_INSTALL_CONFDIR})
endif()

if(BUILD_TESTING)
    add_subdirectory(tests)
endif()

feature_summary(WHAT ALL INCLUDE_QUIET_PACKAGES FATAL_ON_MISSING_REQUIRED_PACKAGES)
