# craftDriver — extensão do Nautilus (nautilus-python, API 4.0 / Nautilus 43+).
#
# - Emblemas de status a partir do xattr user.onedrive.status que o FUSE do craftDriver expõe.
# - Submenu "craftDriver" (Sempre manter / Liberar espaço / Status / Copiar link / Versões) só quando TODOS os itens
#   selecionados têm esse xattr, ou seja, estão dentro do ponto de montagem (qualquer que seja ele).
#
# Instalação (usuário):  ~/.local/share/nautilus-python/extensions/craftdriver.py
# Instalação (sistema):  /usr/share/nautilus-python/extensions/craftdriver.py
# Requer o pacote nautilus-python (Arch: python-nautilus, Debian/Ubuntu: python3-nautilus,
# Fedora: nautilus-python). Depois: "nautilus -q" para recarregar.
#
# Licença MIT, como o resto do craftDriver.

import os
import queue
import shutil
import subprocess
import threading
from urllib.parse import unquote, urlparse

import gi

gi.require_version("Nautilus", "4.0")
from gi.repository import GLib, GObject, Nautilus  # noqa: E402

STATUS_XATTR = "user.onedrive.status"

# Ícones candidatos por status, em ordem de preferência; usa o primeiro que existe no tema
# (Breeze/Papirus têm os primeiros; o Adwaita 4x só tem os "-symbolic" genéricos).
# "Folder" e valores desconhecidos não ganham emblema.
EMBLEMS = {
    "OnlineOnly": ["folder-cloud", "weather-overcast-symbolic"],
    "Available": ["emblem-default-symbolic", "object-select-symbolic", "emblem-ok-symbolic"],
    "AlwaysAvailable": ["vcs-normal", "emblem-default", "checkbox-checked-symbolic"],
    "Syncing": ["emblem-synchronizing", "emblem-synchronizing-symbolic", "vcs-update-required"],
    "LocalOnly": ["emblem-unavailable", "action-unavailable-symbolic", "emblem-unreadable"],
}

# Mesmos textos/comandos do servicemenu do Dolphin (packaging/share/kio/servicemenus/craftdriver.desktop).
ACTIONS = [
    ("pin", "Sempre manter neste dispositivo", "pin"),
    ("free", "Liberar espaço", "unpin"),
    ("status", "Status no craftDriver", "status"),
    ("share", "Copiar link de compartilhamento", "share"),
    ("versions", "Versões anteriores…", "versions"),
]

# "versions" abre a janela do app: sem --notify (que avisaria "Abrindo…"); erros o próprio CLI notifica.
NO_NOTIFY = {"versions"}

SYNCING_POLL_MS = 2000


def read_status(path):
    """Valor de user.onedrive.status, ou None se o item não é do craftDriver (ou não existe)."""
    try:
        value = os.getxattr(path, STATUS_XATTR, follow_symlinks=False)
    except OSError:
        return None
    value = value.decode("utf-8", "replace").strip("\0").strip()
    return value or None


def local_path(file_info):
    """Caminho local de um Nautilus.FileInfo, ou None para URIs não locais (sftp://, trash://...)."""
    uri = file_info.get_uri()
    parsed = urlparse(uri)
    if parsed.scheme != "file":
        return None
    return unquote(parsed.path)


def _icon_exists_default():
    """Função name -> bool que consulta o tema de ícones do GTK 4 (None se não der para consultar)."""
    try:
        gi.require_version("Gtk", "4.0")
        from gi.repository import Gdk, Gtk

        display = Gdk.Display.get_default()
        if display is None:
            return None
        theme = Gtk.IconTheme.get_for_display(display)
        return theme.has_icon
    except Exception:  # noqa: BLE001 — sem GTK (testes) ou versão diferente: usa o 1º candidato
        return None


def emblem_for(status, icon_exists=None):
    """Emblema para o status: o primeiro candidato que existe no tema, ou o primeiro da lista."""
    candidates = EMBLEMS.get(status or "", [])
    if not candidates:
        return None
    if icon_exists is not None:
        for name in candidates:
            try:
                if icon_exists(name):
                    return name
            except Exception:  # noqa: BLE001
                break
    return candidates[0]


def craftdriver_binary():
    """CLI do craftDriver: $CRAFTDRIVER_BIN, PATH ou ~/.local/bin (instalação em modo usuário)."""
    env = os.environ.get("CRAFTDRIVER_BIN")
    if env:
        return env
    found = shutil.which("craftdriver")
    if found:
        return found
    local = os.path.expanduser("~/.local/bin/craftdriver")
    return local if os.access(local, os.X_OK) else None


def selection_paths(files):
    """(caminhos, status) se todos os itens são do craftDriver; senão None (sem menu)."""
    paths, statuses = [], []
    for f in files:
        path = local_path(f)
        if path is None:
            return None
        status = read_status(path)
        if status is None:
            return None
        paths.append(path)
        statuses.append(status)
    if not paths:
        return None
    return paths, statuses


class CraftDriverExtension(GObject.GObject, Nautilus.InfoProvider, Nautilus.MenuProvider):
    def __init__(self):
        super().__init__()
        self._icon_exists = None
        self._icon_checked = False
        # Itens "Syncing" vistos: relidos a cada SYNCING_POLL_MS até mudarem (o FUSE não avisa).
        self._syncing = {}  # caminho -> Nautilus.FileInfo
        self._poll_id = 0
        self._handles = {}  # handle -> Event (cancelamento)
        self._queue = queue.Queue()
        self._worker = None

    # ---------- emblemas ----------

    def _emblem(self, status):
        if not self._icon_checked:
            self._icon_exists = _icon_exists_default()
            self._icon_checked = True
        return emblem_for(status, self._icon_exists)

    def update_file_info_full(self, provider, handle, closure, file_info):
        path = local_path(file_info)
        if path is None:
            return Nautilus.OperationResult.COMPLETE
        # getxattr num FUSE passa pelo processo do craftDriver: lê numa thread única (fila), fora da
        # thread da interface, e completa a operação no loop principal.
        cancelled = threading.Event()
        self._handles[handle] = cancelled
        self._queue.put((provider, handle, closure, file_info, path, cancelled))
        self._ensure_worker()
        return Nautilus.OperationResult.IN_PROGRESS

    def _ensure_worker(self):
        if self._worker is None or not self._worker.is_alive():
            self._worker = threading.Thread(target=self._work, name="craftdriver-xattr", daemon=True)
            self._worker.start()

    def _work(self):
        while True:
            provider, handle, closure, file_info, path, cancelled = self._queue.get()
            status = None if cancelled.is_set() else read_status(path)
            GLib.idle_add(self._finish, provider, handle, closure, file_info, path, status, cancelled)

    def _finish(self, provider, handle, closure, file_info, path, status, cancelled):
        self._handles.pop(handle, None)
        if cancelled.is_set():
            return False
        self.apply_status(file_info, path, status)
        Nautilus.info_provider_update_complete_invoke(closure, provider, handle, Nautilus.OperationResult.COMPLETE)
        return False  # GLib.idle_add: não repetir

    def cancel_update(self, provider, handle):
        event = self._handles.pop(handle, None)
        if event is not None:
            event.set()

    def apply_status(self, file_info, path, status):
        emblem = self._emblem(status)
        if emblem:
            file_info.add_emblem(emblem)
        if status == "Syncing":
            self._syncing[path] = file_info
            self._ensure_poll()
        else:
            self._syncing.pop(path, None)

    def _ensure_poll(self):
        if not self._poll_id:
            self._poll_id = GLib.timeout_add(SYNCING_POLL_MS, self._poll_syncing)

    def _poll_syncing(self):
        for path, file_info in list(self._syncing.items()):
            if read_status(path) != "Syncing":
                del self._syncing[path]
                file_info.invalidate_extension_info()  # o Nautilus chama update_file_info de novo
        if not self._syncing:
            self._poll_id = 0
            return False
        return True

    # ---------- menu ----------

    def get_file_items(self, *args):
        # Nautilus 4.0 passa (files); versões antigas do nautilus-python passavam (window, files).
        files = args[-1]
        selection = selection_paths(files)
        if selection is None:
            return []
        paths, statuses = selection

        top = Nautilus.MenuItem(name="CraftDriver::menu", label="craftDriver", tip="", icon="craftdriver")
        submenu = Nautilus.Menu()
        top.set_submenu(submenu)
        all_pinned = all(s == "AlwaysAvailable" for s in statuses)
        all_online = all(s == "OnlineOnly" for s in statuses)
        for key, label, command in ACTIONS:
            item = Nautilus.MenuItem(name="CraftDriver::" + key, label=label, tip="", icon="")
            single_file = len(paths) == 1 and statuses[0] != "Folder"
            if (key == "pin" and all_pinned) or (key == "free" and all_online) or (key == "versions" and not single_file):
                item.set_property("sensitive", False)
            item.connect("activate", self._run, command, paths, list(files))
            submenu.append_item(item)
        return [top]

    def _run(self, _item, command, paths, files):
        binary = craftdriver_binary()
        if binary is None:
            print("craftDriver: comando \"craftdriver\" não encontrado no PATH")
            return
        try:
            flags = [] if command in NO_NOTIFY else ["--notify"]
            proc = subprocess.Popen([binary, command, *flags, *paths], start_new_session=True,
                                    stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            # Recolhe o processo filho para não deixar zumbis no Nautilus (que fica aberto por horas).
            threading.Thread(target=proc.wait, name="craftdriver-cli", daemon=True).start()
        except OSError as e:
            print(f"craftDriver: falha ao executar {binary}: {e}")
            return
        # O status muda logo depois (Syncing → AlwaysAvailable/OnlineOnly): pede releitura algumas vezes.
        remaining = [3]

        def refresh():
            for f in files:
                f.invalidate_extension_info()
            remaining[0] -= 1
            return remaining[0] > 0

        GLib.timeout_add(1500, refresh)
