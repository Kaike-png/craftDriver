# Testa o instalador do uca.xml do Thunar (sem Thunar): mescla idempotente, backup, --command e --remove.
# Rodar: python3 -m unittest discover -s extensions/thunar/tests -v
import os
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parent / "install-uca.py"
SNIPPET = HERE.parent / "uca.xml"

EXISTING = """<?xml version="1.0" encoding="UTF-8"?>
<actions>
<action>
\t<icon>utilities-terminal</icon>
\t<name>Abrir terminal aqui</name>
\t<unique-id>1700000000000000-1</unique-id>
\t<command>exo-open --working-directory %f --launch TerminalEmulator</command>
\t<description>Exemplo</description>
\t<patterns>*</patterns>
\t<startup-notify/>
\t<directories/>
</action>
</actions>
"""


class InstallUca(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.config = Path(self.tmp.name)
        self.uca = self.config / "Thunar" / "uca.xml"
        self.sysdirs = Path(self.tmp.name) / "xdg-vazio"

    def tearDown(self):
        self.tmp.cleanup()

    def run_script(self, *args):
        # XDG_CONFIG_DIRS isolado: o /etc/xdg/Thunar/uca.xml da máquina não pode entrar no teste
        env = dict(os.environ, XDG_CONFIG_HOME=str(self.config), XDG_CONFIG_DIRS=str(self.sysdirs))
        r = subprocess.run([sys.executable, str(SCRIPT), *args], env=env, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        return r.stdout

    def actions(self):
        root = ET.parse(self.uca).getroot()
        self.assertEqual(root.tag, "actions")
        return [(a.findtext("unique-id"), a.findtext("command"), a.findtext("submenu")) for a in root.findall("action")]

    def test_snippet_is_valid(self):
        root = ET.parse(SNIPPET).getroot()
        acts = root.findall("action")
        self.assertEqual([a.findtext("unique-id") for a in acts], ["craftdriver-pin", "craftdriver-free", "craftdriver-status", "craftdriver-share", "craftdriver-versions"])
        self.assertEqual([a.findtext("command") for a in acts],
                         ["craftdriver pin --notify %F", "craftdriver unpin --notify %F", "craftdriver status --notify %F",
                          "craftdriver share --notify %F", "craftdriver versions %f"])
        for a in acts:
            for tag in ("other-files", "text-files", "image-files", "audio-files", "video-files"):
                self.assertIsNotNone(a.find(tag), tag)
            # versões existem só para arquivos: essa ação não aparece em pastas
            self.assertEqual(a.find("directories") is None, a.findtext("unique-id") == "craftdriver-versions")

    def test_creates_file_when_missing(self):
        self.run_script()
        self.assertEqual([a[0] for a in self.actions()], ["craftdriver-pin", "craftdriver-free", "craftdriver-status", "craftdriver-share", "craftdriver-versions"])

    def test_new_file_keeps_system_default_actions(self):
        # Sem uca.xml do usuário o Thunar usa o do sistema ($XDG_CONFIG_DIRS/Thunar/uca.xml, ex.: "Abrir terminal
        # aqui"); criar um arquivo só com as ações do craftDriver esconderia essas ações padrão.
        sysdir = Path(self.tmp.name) / "xdg-sistema"
        (sysdir / "Thunar").mkdir(parents=True)
        (sysdir / "Thunar" / "uca.xml").write_text(EXISTING)
        self.sysdirs = f"{self.tmp.name}/nao-existe:{sysdir}"
        self.run_script()
        self.assertEqual([a[0] for a in self.actions()][0], "1700000000000000-1")
        self.assertEqual(len(self.actions()), 6)

    def test_merge_is_idempotent_and_keeps_user_actions(self):
        self.uca.parent.mkdir(parents=True)
        self.uca.write_text(EXISTING)
        self.run_script()
        self.run_script()  # 2ª vez não duplica
        ids = [a[0] for a in self.actions()]
        self.assertEqual(ids, ["1700000000000000-1", "craftdriver-pin", "craftdriver-free", "craftdriver-status", "craftdriver-share", "craftdriver-versions"])
        self.assertTrue((self.uca.parent / "uca.xml.bak-craftdriver").exists())
        # A ação do usuário continua com as tags vazias (ex. <startup-notify/>).
        user = ET.parse(self.uca).getroot().findall("action")[0]
        self.assertIsNotNone(user.find("startup-notify"))

    def test_command_override(self):
        self.run_script("--command", "/home/u/.local/bin/craftdriver")
        self.assertEqual({a[1] for a in self.actions()},
                         {"/home/u/.local/bin/craftdriver pin --notify %F", "/home/u/.local/bin/craftdriver unpin --notify %F",
                          "/home/u/.local/bin/craftdriver status --notify %F", "/home/u/.local/bin/craftdriver share --notify %F",
                          "/home/u/.local/bin/craftdriver versions %f"})

    def test_remove_only_ours(self):
        self.uca.parent.mkdir(parents=True)
        self.uca.write_text(EXISTING)
        self.run_script()
        self.run_script("--remove")
        self.assertEqual([a[0] for a in self.actions()], ["1700000000000000-1"])

    def test_remove_without_file(self):
        self.assertIn("nada a remover", self.run_script("--remove"))
        self.assertFalse(self.uca.exists())


if __name__ == "__main__":
    unittest.main()
