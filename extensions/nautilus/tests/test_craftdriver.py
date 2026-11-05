# Testes da extensão do Nautilus SEM o Nautilus: um módulo "gi" falso (stub) imita a API do
# nautilus-python 4.0 o suficiente para exercitar status → emblema, o filtro de arquivos do menu,
# o fluxo assíncrono (update_file_info_full) e a execução do CLI.
#
# Rodar: python3 -m unittest discover -s extensions/nautilus/tests -v
# Usa arquivos reais com o xattr user.onedrive.status (precisa de um FS com user xattrs; diretório:
# $CRAFTDRIVER_TEST_DIR ou o temporário padrão). Sem suporte, os testes de xattr são pulados.
import importlib.util
import os
import stat
import sys
import tempfile
import time
import types
import unittest
from pathlib import Path
from urllib.parse import quote

HERE = Path(__file__).resolve().parent
EXTENSION = HERE.parent / "craftdriver.py"


# ---------------------------------------------------------------- stub do gi
class _GObjectBase:
    def __init__(self, **props):
        self._props = dict(props)
        self._handlers = {}

    def set_property(self, name, value):
        self._props[name] = value

    def get_property(self, name):
        return self._props.get(name)

    def connect(self, signal, callback, *args):
        self._handlers.setdefault(signal, []).append((callback, args))

    def emit(self, signal):
        for callback, args in self._handlers.get(signal, []):
            callback(self, *args)


class _MenuItem(_GObjectBase):
    def __init__(self, **props):
        super().__init__(**props)
        self._props.setdefault("sensitive", True)
        self.submenu = None

    def set_submenu(self, menu):
        self.submenu = menu


class _Menu:
    def __init__(self):
        self.items = []

    def append_item(self, item):
        self.items.append(item)


class _OperationResult:
    COMPLETE = "COMPLETE"
    IN_PROGRESS = "IN_PROGRESS"
    FAILED = "FAILED"


class _GLib:
    """Loop principal falso: idle_add/timeout_add só enfileiram; o teste roda com pump()."""

    idle = []
    timeouts = []

    @classmethod
    def idle_add(cls, fn, *args):
        cls.idle.append((fn, args))
        return len(cls.idle)

    @classmethod
    def timeout_add(cls, ms, fn, *args):
        cls.timeouts.append((ms, fn, args))
        return len(cls.timeouts)

    @classmethod
    def run_idle(cls):
        pending, cls.idle = cls.idle, []
        for fn, args in pending:
            fn(*args)
        return len(pending)

    @classmethod
    def fire_timeouts(cls):
        """Dispara cada timeout uma vez; os que devolvem True continuam agendados."""
        pending, cls.timeouts = cls.timeouts, []
        for ms, fn, args in pending:
            if fn(*args):
                cls.timeouts.append((ms, fn, args))


completed = []


def _install_fake_gi():
    gi = types.ModuleType("gi")
    gi.required = {}

    def require_version(name, version):
        if name == "Gtk":
            raise ValueError("sem GTK no stub")  # força o caminho "sem tema": 1º candidato
        gi.required[name] = version

    gi.require_version = require_version
    repo = types.ModuleType("gi.repository")
    gobject = types.ModuleType("gi.repository.GObject")
    gobject.GObject = type("GObject", (object,), {"__init__": lambda self, *a, **k: None})
    nautilus = types.ModuleType("gi.repository.Nautilus")
    nautilus.InfoProvider = type("InfoProvider", (object,), {})
    nautilus.MenuProvider = type("MenuProvider", (object,), {})
    nautilus.MenuItem = _MenuItem
    nautilus.Menu = _Menu
    nautilus.OperationResult = _OperationResult
    nautilus.info_provider_update_complete_invoke = lambda closure, provider, handle, result: completed.append(
        (closure, handle, result))
    repo.GObject = gobject
    repo.Nautilus = nautilus
    repo.GLib = _GLib
    gi.repository = repo
    sys.modules.update({"gi": gi, "gi.repository": repo, "gi.repository.GObject": gobject,
                        "gi.repository.Nautilus": nautilus, "gi.repository.GLib": _GLib})
    return gi


class FakeFileInfo:
    def __init__(self, path=None, uri=None):
        self.uri = uri or ("file://" + quote(path))
        self.emblems = []
        self.invalidated = 0

    def get_uri(self):
        return self.uri

    def add_emblem(self, name):
        self.emblems.append(name)

    def invalidate_extension_info(self):
        self.invalidated += 1


def _load_extension():
    gi = _install_fake_gi()
    spec = importlib.util.spec_from_file_location("craftdriver_nautilus", EXTENSION)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return gi, module


GI, CD = _load_extension()


def _xattr_supported(directory):
    probe = Path(directory) / ".probe"
    probe.write_text("x")
    try:
        os.setxattr(probe, CD.STATUS_XATTR, b"x")
        return True
    except OSError:
        return False
    finally:
        probe.unlink()


class StatusTable(unittest.TestCase):
    def test_requires_nautilus_4(self):
        self.assertEqual(GI.required.get("Nautilus"), "4.0")

    def test_first_candidate_without_theme(self):
        self.assertEqual(CD.emblem_for("OnlineOnly"), "folder-cloud")
        self.assertEqual(CD.emblem_for("Available"), "emblem-default-symbolic")
        self.assertEqual(CD.emblem_for("AlwaysAvailable"), "vcs-normal")
        self.assertEqual(CD.emblem_for("Syncing"), "emblem-synchronizing")
        self.assertEqual(CD.emblem_for("LocalOnly"), "emblem-unavailable")
        for status in ("Folder", "Desconhecido", "", None):
            self.assertIsNone(CD.emblem_for(status))

    def test_picks_first_icon_in_theme(self):
        # Ícones que o Adwaita 51 tem de fato (/usr/share/icons/Adwaita).
        adwaita = {"weather-overcast-symbolic", "object-select-symbolic", "checkbox-checked-symbolic",
                   "emblem-synchronizing", "action-unavailable-symbolic"}
        got = {s: CD.emblem_for(s, adwaita.__contains__) for s in CD.EMBLEMS}
        self.assertEqual(got, {"OnlineOnly": "weather-overcast-symbolic", "Available": "object-select-symbolic",
                               "AlwaysAvailable": "checkbox-checked-symbolic", "Syncing": "emblem-synchronizing",
                               "LocalOnly": "action-unavailable-symbolic"})
        # Emblemas distintos para cada status (senão não dá para diferenciar na tela).
        self.assertEqual(len(set(got.values())), len(got))

    def test_local_path(self):
        self.assertEqual(CD.local_path(FakeFileInfo(uri="file:///tmp/a%20b/c%C3%A7.txt")), "/tmp/a b/cç.txt")
        self.assertIsNone(CD.local_path(FakeFileInfo(uri="sftp://host/x")))
        self.assertIsNone(CD.local_path(FakeFileInfo(uri="trash:///x")))


class WithFiles(unittest.TestCase):
    def setUp(self):
        base = os.environ.get("CRAFTDRIVER_TEST_DIR") or None
        self.tmp = tempfile.TemporaryDirectory(prefix="craftdriver-nautilus-", dir=base)
        self.dir = Path(self.tmp.name)
        if not _xattr_supported(self.dir):
            self.skipTest("FS sem user xattrs")
        self.files = {}
        for status in ("OnlineOnly", "Available", "AlwaysAvailable", "Syncing", "LocalOnly"):
            self.files[status] = self._make(f"{status} ç.txt", status)
        folder = self.dir / "Pasta"
        folder.mkdir()
        os.setxattr(folder, CD.STATUS_XATTR, b"Folder")
        self.files["Folder"] = str(folder)
        self.outside = self._make("fora.txt", None)
        _GLib.idle.clear()
        _GLib.timeouts.clear()
        completed.clear()
        self.ext = CD.CraftDriverExtension()

    def tearDown(self):
        self.tmp.cleanup()

    def _make(self, name, status):
        p = self.dir / name
        p.write_text("x")
        if status:
            os.setxattr(p, CD.STATUS_XATTR, status.encode())
        return str(p)

    def _update(self, path):
        """Roda update_file_info_full + a thread de leitura + o loop principal falso."""
        info = FakeFileInfo(path)
        handle = object()
        result = self.ext.update_file_info_full("provider", handle, "closure", info)
        self.assertEqual(result, _OperationResult.IN_PROGRESS)
        deadline = time.time() + 5
        while not _GLib.idle and time.time() < deadline:
            time.sleep(0.01)
        _GLib.run_idle()
        self.assertIn(("closure", handle, _OperationResult.COMPLETE), completed)
        return info

    def test_read_status(self):
        self.assertEqual(CD.read_status(self.files["Syncing"]), "Syncing")
        self.assertIsNone(CD.read_status(self.outside))
        self.assertIsNone(CD.read_status(str(self.dir / "nao-existe")))

    def test_emblems_async(self):
        expected = {"OnlineOnly": ["folder-cloud"], "Available": ["emblem-default-symbolic"],
                    "AlwaysAvailable": ["vcs-normal"], "Syncing": ["emblem-synchronizing"],
                    "LocalOnly": ["emblem-unavailable"], "Folder": []}
        for status, emblems in expected.items():
            self.assertEqual(self._update(self.files[status]).emblems, emblems, status)
        self.assertEqual(self._update(self.outside).emblems, [])
        # URI não local: completa na hora, sem thread.
        remote = FakeFileInfo(uri="sftp://host/x")
        self.assertEqual(self.ext.update_file_info_full("p", object(), "c", remote), _OperationResult.COMPLETE)

    def test_cancelled_update_does_not_touch_file(self):
        info = FakeFileInfo(self.files["Available"])
        handle = object()
        # Cancela antes da thread terminar: segura a fila até o cancelamento.
        self.ext._worker = object.__new__(type("Busy", (), {"is_alive": lambda self: True}))
        self.ext.update_file_info_full("p", handle, "c", info)
        self.ext.cancel_update("p", handle)
        self.ext._worker = None
        self.ext._ensure_worker()
        deadline = time.time() + 5
        while not _GLib.idle and time.time() < deadline:
            time.sleep(0.01)
        _GLib.run_idle()
        self.assertEqual(info.emblems, [])
        self.assertEqual(completed, [])

    def test_syncing_is_polled_until_it_changes(self):
        info = self._update(self.files["Syncing"])
        self.assertEqual(len(_GLib.timeouts), 1)
        _GLib.fire_timeouts()  # ainda sincronizando: continua agendado, não invalida
        self.assertEqual(info.invalidated, 0)
        self.assertEqual(len(_GLib.timeouts), 1)
        os.setxattr(self.files["Syncing"], CD.STATUS_XATTR, b"AlwaysAvailable")
        _GLib.fire_timeouts()
        self.assertEqual(info.invalidated, 1)
        self.assertEqual(_GLib.timeouts, [])  # nada mais sincronizando: parou o timer

    def _menu(self, *paths):
        return self.ext.get_file_items([FakeFileInfo(p) for p in paths])

    def test_menu_only_for_craftdriver_items(self):
        self.assertEqual(self._menu(self.outside), [])
        self.assertEqual(self._menu(self.files["OnlineOnly"], self.outside), [])
        self.assertEqual(self.ext.get_file_items([FakeFileInfo(uri="sftp://h/x")]), [])
        self.assertEqual(self.ext.get_file_items([]), [])
        items = self._menu(self.files["OnlineOnly"], self.files["Folder"])
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0].get_property("label"), "craftDriver")
        labels = [i.get_property("label") for i in items[0].submenu.items]
        self.assertEqual(labels, ["Sempre manter neste dispositivo", "Liberar espaço", "Status no craftDriver",
                                  "Copiar link de compartilhamento", "Versões anteriores…"])
        # Assinatura antiga (window, files) também funciona.
        self.assertEqual(len(self.ext.get_file_items("janela", [FakeFileInfo(self.files["Available"])])), 1)

    def test_menu_sensitivity(self):
        sub = lambda *p: {i.get_property("name"): i.get_property("sensitive") for i in self._menu(*p)[0].submenu.items}
        always = {"CraftDriver::status": True, "CraftDriver::share": True, "CraftDriver::versions": True}
        self.assertEqual(sub(self.files["AlwaysAvailable"]), {"CraftDriver::pin": False, "CraftDriver::free": True, **always})
        self.assertEqual(sub(self.files["OnlineOnly"]), {"CraftDriver::pin": True, "CraftDriver::free": False, **always})
        # versões: só um arquivo (nem pasta, nem vários)
        both = sub(self.files["OnlineOnly"], self.files["AlwaysAvailable"])
        self.assertFalse(both.pop("CraftDriver::versions"))
        self.assertEqual(set(both.values()), {True})
        self.assertFalse(sub(self.files["Folder"])["CraftDriver::versions"])
        self.assertTrue(sub(self.files["Folder"])["CraftDriver::share"])

    def test_activate_runs_cli(self):
        log = self.dir / "cli.log"
        fake = self.dir / "fake-craftdriver"
        fake.write_text(f"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{log}'\n")
        fake.chmod(stat.S_IRWXU)
        os.environ["CRAFTDRIVER_BIN"] = str(fake)
        try:
            for key, command in (("pin", "pin"), ("free", "unpin"), ("status", "status"), ("share", "share"), ("versions", "versions")):
                log.unlink(missing_ok=True)
                single = key == "versions"
                files = [FakeFileInfo(self.files["OnlineOnly"])] + ([] if single else [FakeFileInfo(self.files["AlwaysAvailable"])])
                menu = self.ext.get_file_items(files)[0]
                item = next(i for i in menu.submenu.items if i.get_property("name") == "CraftDriver::" + key)
                item.emit("activate")
                deadline = time.time() + 5
                while not (log.exists() and log.stat().st_size) and time.time() < deadline:
                    time.sleep(0.02)
                paths = [self.files["OnlineOnly"]] + ([] if single else [self.files["AlwaysAvailable"]])
                expected = [command] + ([] if single else ["--notify"]) + paths
                self.assertEqual(log.read_text().splitlines(), expected)
                # Depois do comando, pede ao Nautilus para reler os emblemas.
                _GLib.fire_timeouts()
                self.assertTrue(all(f.invalidated == 1 for f in files))
                _GLib.timeouts.clear()
        finally:
            del os.environ["CRAFTDRIVER_BIN"]

    def test_missing_cli_does_not_raise(self):
        os.environ["CRAFTDRIVER_BIN"] = str(self.dir / "nao-existe")
        try:
            item = self._menu(self.files["OnlineOnly"])[0].submenu.items[2]
            item.emit("activate")  # só registra o erro
        finally:
            del os.environ["CRAFTDRIVER_BIN"]


if __name__ == "__main__":
    unittest.main()
