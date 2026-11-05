# Testes dos plugins: carregam os .so compilados do mesmo jeito que o Dolphin/KIO e usam arquivos
# reais com o xattr user.onedrive.status gravado (precisa de um FS com user xattrs; senão os testes
# que dependem disso são pulados com QSKIP). Diretório dos arquivos: $CRAFTDRIVER_TEST_DIR ou $TMPDIR.
find_package(Qt6 ${QT_MIN_VERSION} REQUIRED COMPONENTS Test)
include(ECMAddTests)

ecm_add_test(overlaytest.cpp
    TEST_NAME craftdriver-overlay
    LINK_LIBRARIES craftdriverstatus KF6::KIOCore KF6::CoreAddons Qt6::Gui Qt6::Test)

ecm_add_test(actionstest.cpp
    TEST_NAME craftdriver-actions
    LINK_LIBRARIES craftdriverstatus KF6::KIOWidgets KF6::KIOCore KF6::CoreAddons Qt6::Widgets Qt6::Test)

# Diagnóstico manual (não é teste automático nem é instalado).
add_executable(craftdriver-overlay-probe probe.cpp)
target_link_libraries(craftdriver-overlay-probe PRIVATE craftdriverstatus KF6::KIOCore KF6::CoreAddons Qt6::Gui)

# Servicemenu genérico do repositório (fallback) e o kservicemenurc que o esconde.
set(_servicemenu "${CMAKE_CURRENT_SOURCE_DIR}/../../../packaging/share/kio/servicemenus/craftdriver.desktop")
target_compile_definitions(craftdriver-actions PRIVATE
    SERVICEMENU_FILE="${_servicemenu}"
    KSERVICEMENURC_FILE="${CMAKE_CURRENT_SOURCE_DIR}/../kservicemenurc")

# Os plugins compilados ficam em <build>/bin/kf6/{overlayicon,kfileitemaction}.
set_tests_properties(craftdriver-overlay craftdriver-actions PROPERTIES
    ENVIRONMENT "QT_PLUGIN_PATH=${CMAKE_BINARY_DIR}/bin;QT_QPA_PLATFORM=offscreen")
