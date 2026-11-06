# craftDriver — extensão do Nautilus (nautilus-python, API 4.0 / Nautilus 43+; API 3.0 / Nautilus 42 como reserva).
#
# - Emblemas de status a partir do xattr user.onedrive.status que o FUSE do craftDriver expõe.
# - Submenu "craftDriver" (Sempre manter / Liberar espaço / Status / Copiar link / Versões) só quando TODOS os itens
#   selecionados estão dentro de uma montagem do craftDriver (fsname=craftdriver na tabela de montagens).
# - Nenhum getxattr roda no loop principal do GLib (um FUSE lento travaria o Nautilus inteiro): leituras numa thread
#   própria; o menu usa o status já lido para os emblemas. Itens fora das montagens nem chegam à thread.
#
# Instalação (usuário):  ~/.local/share/nautilus-python/extensions/craftdriver.py
# Instalação (sistema):  /usr/share/nautilus-python/extensions/craftdriver.py
# Requer o pacote nautilus-python (Arch: python-nautilus, Debian/Ubuntu: python3-nautilus,
# Fedora: nautilus-python). Depois: "nautilus -q" para recarregar.
#
# Licença MIT, como o resto do craftDriver.

import os
import queue
import re
import shutil
import subprocess
import threading
import time
from urllib.parse import unquote, urlparse

import gi

# Nautilus 43+ (GTK 4) traz a API 4.0; o Nautilus 42 (Ubuntu 22.04) só a 3.0, com as mesmas chamadas que usamos.
try:
    gi.require_version("Nautilus", "4.0")
    NAUTILUS_API = "4.0"
except ValueError:
    gi.require_version("Nautilus", "3.0")
    NAUTILUS_API = "3.0"
from gi.repository import GLib, GObject, Nautilus  # noqa: E402

STATUS_XATTR = "user.onedrive.status"

# Montagens do craftDriver: FuseHost monta com -o fsname=craftdriver (1º campo de /proc/self/mounts).
MOUNTS_FILE = "/proc/self/mounts"
FSNAME = "craftdriver"
MOUNTS_TTL_S = 2.0
STATUS_CACHE_MAX = 20000

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
# Item que continua "Syncing" (envio grande, ou preso em Problemas): relido a cada 1, 1, 1, 2, 4, 8, 16 e depois
# 30 ciclos (1 min) — recuo exponencial — e esquecido após SYNCING_EXPIRE_TICKS ciclos (30 min); o emblema volta a ser
# lido quando o Nautilus pedir de novo (ao reabrir a pasta).
SYNCING_BACKOFF = (1, 1, 1, 2, 4, 8, 16, 30)
SYNCING_EXPIRE_TICKS = 900
CANONICAL_CACHE_MAX = 512


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


def _unescape_mount(field):
    """/proc/mounts escreve espaço, tab, \\n e \\ como \\ooo (octal)."""
    return re.sub(r"\\([0-7]{3})", lambda m: chr(int(m.group(1), 8)), field)


def craftdriver_mounts(mounts_file=None):
    """Pontos de montagem do craftDriver (fsname=craftdriver) na tabela de montagens deste processo."""
    try:
        with open(mounts_file or MOUNTS_FILE, encoding="utf-8", errors="surrogateescape") as f:
            lines = f.read().splitlines()
    except OSError:
        return []
    mounts = []
    for line in lines:
        parts = line.split()
        if len(parts) >= 3 and parts[0] == FSNAME and parts[2].startswith("fuse"):
            mounts.append(_unescape_mount(parts[1]))
    return mounts


def inside_mount(path, mounts):
    """O caminho é um ponto de montagem do craftDriver ou fica dentro de um (comparação textual, sem syscalls)."""
    for mount in mounts:
        root = mount.rstrip("/") or "/"
        if path == root or path.startswith(root.rstrip("/") + "/"):
            return True
    return False


def _icon_exists_default():
    """Função name -> bool que consulta o tema de ícones do GTK (None se não der para consultar)."""
    try:
        if NAUTILUS_API == "4.0":
            gi.require_version("Gtk", "4.0")
            from gi.repository import Gdk, Gtk

            display = Gdk.Display.get_default()
            if display is None:
                return None
            return Gtk.IconTheme.get_for_display(display).has_icon
        gi.require_version("Gtk", "3.0")
        from gi.repository import Gtk

        return Gtk.IconTheme.get_default().has_icon
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


def _is_directory(file_info):
    try:
        return bool(file_info.is_directory())
    except Exception:  # noqa: BLE001
        return False


class CraftDriverExtension(GObject.GObject, Nautilus.InfoProvider, Nautilus.MenuProvider):
    def __init__(self):
        super().__init__()
        self._icon_exists = None
        self._icon_checked = False
        # Itens "Syncing" vistos: relidos (com recuo, ver SYNCING_BACKOFF) até mudarem (o FUSE não avisa).
        self._syncing = {}  # caminho -> [Nautilus.FileInfo, nº de releituras, ciclo da próxima, ciclo em que entrou]
        self._tick = 0  # ciclos do timer de SYNCING_POLL_MS
        self._canonical = {}  # pasta -> pasta canônica (realpath), para montagem atrás de symlink
        self._poll_id = 0
        self._polling = False  # releitura dos "Syncing" em curso na thread
        self._status = {}  # caminho -> último status lido (o menu usa isto; não lê xattr na thread da interface)
        self._mounts = []
        self._mounts_at = None
        self._handles = {}  # handle -> Event (cancelamento)
        self._queue = queue.Queue()
        self._worker = None

    # ---------- emblemas ----------

    def _emblem(self, status):
        if not self._icon_checked:
            self._icon_exists = _icon_exists_default()
            self._icon_checked = True
        return emblem_for(status, self._icon_exists)

    def _craftdriver_mounts(self):
        now = time.monotonic()
        if self._mounts_at is None or now - self._mounts_at > MOUNTS_TTL_S:
            self._mounts = craftdriver_mounts()
            self._mounts_at = now
        return self._mounts

    def _in_mount(self, path):
        """Dentro de uma montagem do craftDriver, também quando o caminho passa por um symlink (/proc/mounts traz o
        caminho canônico; ~/OneDrive -> /data/od). O realpath é da PASTA (cache): um por pasta, não por arquivo."""
        mounts = self._craftdriver_mounts()
        if not mounts:
            return False
        if inside_mount(path, mounts):
            return True
        parent, name = os.path.split(path.rstrip("/") or "/")
        real = self._canonical.get(parent)
        if real is None:
            if len(self._canonical) >= CANONICAL_CACHE_MAX:
                self._canonical.clear()
            real = self._canonical[parent] = os.path.realpath(parent)
        return real != parent and inside_mount(os.path.join(real, name), mounts)

    def update_file_info_full(self, provider, handle, closure, file_info):
        path = local_path(file_info)
        # Fora das montagens do craftDriver não há o que ler (antes toda pasta de todo disco ia para a fila).
        if path is None or not self._in_mount(path):
            return Nautilus.OperationResult.COMPLETE
        # getxattr num FUSE passa pelo processo do craftDriver: lê numa thread única (fila), fora da
        # thread da interface, e completa a operação no loop principal.
        cancelled = threading.Event()
        self._handles[handle] = cancelled
        self._queue.put(("info", provider, handle, closure, file_info, path, cancelled))
        self._ensure_worker()
        return Nautilus.OperationResult.IN_PROGRESS

    def _ensure_worker(self):
        if self._worker is None or not self._worker.is_alive():
            self._worker = threading.Thread(target=self._work, name="craftdriver-xattr", daemon=True)
            self._worker.start()

    def _work(self):
        while True:
            job = self._queue.get()
            if job[0] == "poll":
                results = [(path, read_status(path)) for path in job[1]]
                GLib.idle_add(self._finish_poll, results)
                continue
            _, provider, handle, closure, file_info, path, cancelled = job
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
        if len(self._status) >= STATUS_CACHE_MAX:
            self._status.clear()
        self._status[path] = status
        emblem = self._emblem(status)
        if emblem:
            file_info.add_emblem(emblem)
        if status == "Syncing":
            entry = self._syncing.get(path)
            if entry is None:  # novo: releitura no próximo ciclo; já acompanhado: mantém o recuo
                self._syncing[path] = [file_info, 0, self._tick + 1, self._tick]
            else:
                entry[0] = file_info
            self._ensure_poll()
        else:
            self._syncing.pop(path, None)

    def _ensure_poll(self):
        if not self._poll_id:
            self._poll_id = GLib.timeout_add(SYNCING_POLL_MS, self._poll_syncing)

    def _poll_syncing(self):
        # Roda no loop principal: só pede a releitura à thread (um getxattr aqui travaria o Nautilus).
        if not self._syncing and not self._polling:
            self._poll_id = 0
            return False
        self._tick += 1
        for path in [p for p, e in self._syncing.items() if self._tick - e[3] >= SYNCING_EXPIRE_TICKS]:
            del self._syncing[path]  # preso há muito tempo: para de reler (sem polling perpétuo)
        due = [p for p, e in self._syncing.items() if e[2] <= self._tick]
        if not self._polling and due:
            self._polling = True
            for path in due:
                entry = self._syncing[path]
                entry[2] = self._tick + SYNCING_BACKOFF[min(entry[1], len(SYNCING_BACKOFF) - 1)]
                entry[1] += 1
            self._queue.put(("poll", due))
            self._ensure_worker()
        return True

    def _finish_poll(self, results):
        self._polling = False
        for path, status in results:
            if status == "Syncing":
                continue
            entry = self._syncing.pop(path, None)
            if entry is not None:
                file_info = entry[0]
                self._status[path] = status
                file_info.invalidate_extension_info()  # o Nautilus chama update_file_info de novo
        return False  # GLib.idle_add: não repetir

    # ---------- menu ----------

    def get_file_items(self, *args):
        # Nautilus 4.0 passa (files); versões antigas do nautilus-python passavam (window, files).
        files = args[-1]
        if not files:
            return []
        paths, statuses = [], []
        for f in files:
            path = local_path(f)
            if path is None or not self._in_mount(path):
                return []  # algum item fora do craftDriver: sem menu
            paths.append(path)
            # status dos emblemas (já lido na thread); None = ainda não lido: tudo habilitado
            statuses.append(self._status.get(path))

        top = Nautilus.MenuItem(name="CraftDriver::menu", label="craftDriver", tip="", icon="craftdriver")
        submenu = Nautilus.Menu()
        top.set_submenu(submenu)
        all_pinned = all(s == "AlwaysAvailable" for s in statuses)
        all_online = all(s == "OnlineOnly" for s in statuses)
        for key, label, command in ACTIONS:
            item = Nautilus.MenuItem(name="CraftDriver::" + key, label=label, tip="", icon="")
            single_file = len(paths) == 1 and statuses[0] != "Folder" and not (statuses[0] is None and _is_directory(files[0]))
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
