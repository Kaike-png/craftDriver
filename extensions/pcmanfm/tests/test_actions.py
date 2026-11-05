# Testa as ações do PCManFM/PCManFM-Qt sem o gerenciador: valida os .desktop e executa a condição
# ShowIfTrue como o libfm-qt faz (expande %F com g_shell_quote e roda com system()), em arquivos
# reais com e sem o xattr user.onedrive.status. Precisa do getfattr (pacote attr) e de user xattrs.
# Rodar: python3 -m unittest discover -s extensions/pcmanfm/tests -v
import configparser
import os
import shlex
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ACTIONS = HERE.parent
XATTR = "user.onedrive.status"


def load(name):
    cp = configparser.ConfigParser(interpolation=None, strict=True)
    cp.optionxform = str  # chaves diferenciam maiúsculas
    cp.read(ACTIONS / name, encoding="utf-8")
    return cp


def expand(template, files):
    # libfm-qt (fileaction.cpp): %F = caminhos com g_shell_quote separados por espaço.
    return template.replace("%F", " ".join(shlex.quote(f) for f in files))


class Actions(unittest.TestCase):
    def test_menu_lists_all_actions(self):
        menu = load("craftdriver-menu.desktop")["Desktop Entry"]
        self.assertEqual(menu["Type"], "Menu")
        ids = [i for i in menu["ItemsList"].split(";") if i]
        self.assertEqual(ids, ["craftdriver-pin", "craftdriver-free", "craftdriver-status", "craftdriver-share", "craftdriver-versions"])
        for i in ids:
            self.assertTrue((ACTIONS / f"{i}.desktop").exists(), i)

    def test_action_files(self):
        expected = {"pin": "pin --notify %F", "free": "unpin --notify %F", "status": "status --notify %F",
                    "share": "share --notify %F", "versions": "versions %f"}
        for key, cmd in expected.items():
            cp = load(f"craftdriver-{key}.desktop")
            entry = cp["Desktop Entry"]
            self.assertEqual(entry["Type"], "Action")
            self.assertEqual(entry["Profiles"], "craftdriver;")
            profile = cp["X-Action-Profile craftdriver"]
            self.assertEqual(profile["Exec"], f"craftdriver {cmd}")
            self.assertEqual(profile["TryExec"], "craftdriver")
            self.assertIn("%F", profile["ShowIfTrue"])
        # versões: um arquivo só (sem --notify: abre a janela do app)
        versions = load("craftdriver-versions.desktop")["X-Action-Profile craftdriver"]
        self.assertEqual(versions["SelectionCount"], "=1")
        self.assertEqual(versions["MimeTypes"], "all/allfiles;")


@unittest.skipUnless(shutil.which("getfattr"), "getfattr (pacote attr) não instalado")
class ShowIfTrue(unittest.TestCase):
    def setUp(self):
        base = os.environ.get("CRAFTDRIVER_TEST_DIR") or None
        self.tmp = tempfile.TemporaryDirectory(prefix="craftdriver-pcmanfm-", dir=base)
        d = Path(self.tmp.name)
        self.inside = d / "nuvem.txt"
        self.inside.write_text("x")
        try:
            os.setxattr(self.inside, XATTR, b"OnlineOnly")
        except OSError:
            self.skipTest("FS sem user xattrs")
        self.tricky = d / "it's a \"file\" $(touch pwned) ç.txt"  # nome hostil: aspas, $( ), acento
        self.tricky.write_text("x")
        os.setxattr(self.tricky, XATTR, b"AlwaysAvailable")
        self.folder = d / "Pasta com espaço"
        self.folder.mkdir()
        os.setxattr(self.folder, XATTR, b"Folder")
        self.outside = d / "fora.txt"
        self.outside.write_text("x")
        self.dir = d
        self.cond = load("craftdriver-pin.desktop")["X-Action-Profile craftdriver"]["ShowIfTrue"]

    def tearDown(self):
        self.tmp.cleanup()

    def check(self, files):
        r = subprocess.run(expand(self.cond, [str(f) for f in files]), shell=True, capture_output=True, text=True,
                           cwd=self.dir)
        return r.returncode, r.stdout.strip()

    def test_inside_shows(self):
        self.assertEqual(self.check([self.inside]), (0, "true"))
        self.assertEqual(self.check([self.inside, self.tricky, self.folder]), (0, "true"))
        self.assertFalse((self.dir / "pwned").exists())

    def test_outside_or_mixed_hides(self):
        self.assertEqual(self.check([self.outside]), (1, ""))
        self.assertEqual(self.check([self.inside, self.outside]), (1, ""))
        self.assertEqual(self.check([self.dir / "nao-existe"]), (1, ""))

    def test_conditions_identical_in_all_actions(self):
        for key in ("free", "status", "share", "versions"):
            self.assertEqual(load(f"craftdriver-{key}.desktop")["X-Action-Profile craftdriver"]["ShowIfTrue"], self.cond)


if __name__ == "__main__":
    unittest.main()
