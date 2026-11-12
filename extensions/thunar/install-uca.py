#!/usr/bin/env python3
"""Mescla as ações do CraftDriver no uca.xml do Thunar (ou as remove com --remove).

Uso: install-uca.py [--remove] [--uca CAMINHO] [--command CMD]
  --uca      arquivo de destino (padrão: $XDG_CONFIG_HOME/Thunar/uca.xml)
  --command  troca "craftdriver" no início dos comandos (ex.: ~/.local/bin/craftdriver)

Idempotente: ações com <unique-id> craftdriver-* são substituídas, as outras ficam intactas. Sem uca.xml do
usuário, parte do uca.xml do sistema ($XDG_CONFIG_DIRS/Thunar/uca.xml) para não esconder as ações padrão.
Antes de alterar um uca.xml existente, grava uma cópia em uca.xml.bak-craftdriver.
O Thunar relê o arquivo ao abrir uma nova janela (ou: thunar -q).
"""
import argparse
import os
import shutil
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
PREFIX = "craftdriver-"


def default_uca():
    config = os.environ.get("XDG_CONFIG_HOME") or os.path.expanduser("~/.config")
    return Path(config) / "Thunar" / "uca.xml"


def system_uca():
    """uca.xml que o Thunar usa enquanto o usuário não tem o seu ($XDG_CONFIG_DIRS, padrão /etc/xdg)."""
    for d in (os.environ.get("XDG_CONFIG_DIRS") or "/etc/xdg").split(":"):
        p = Path(d) / "Thunar" / "uca.xml" if d else None
        if p and p.is_file():
            return p
    return None


def snippet_path():
    # No repositório fica ao lado deste script; nos pacotes, em /usr/share/craftdriver/thunar/.
    for p in (HERE / "uca.xml", Path("/usr/share/craftdriver/thunar/uca.xml")):
        if p.exists():
            return p
    sys.exit("install-uca: uca.xml do CraftDriver não encontrado")


def ours(action):
    uid = action.findtext("unique-id") or ""
    return uid.startswith(PREFIX)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--remove", action="store_true")
    ap.add_argument("--uca", type=Path, default=None)
    ap.add_argument("--command", default=None)
    args = ap.parse_args(argv)
    uca = args.uca or default_uca()

    if uca.exists():
        tree = ET.parse(uca)
        root = tree.getroot()
        if root.tag != "actions":
            sys.exit(f"install-uca: {uca} não parece um uca.xml (raiz <{root.tag}>)")
        backup = uca.with_name(uca.name + ".bak-craftdriver")
        shutil.copy2(uca, backup)
    else:
        if args.remove:
            print(f"{uca} não existe; nada a remover")
            return 0
        # parte do uca.xml do sistema: um arquivo só com as nossas ações esconderia as padrão ("Abrir terminal aqui")
        system = system_uca()
        tree = ET.parse(system) if system else None
        if tree is None or tree.getroot().tag != "actions":
            tree = ET.ElementTree(ET.Element("actions"))
        root = tree.getroot()

    removed = [a for a in root.findall("action") if ours(a)]
    for a in removed:
        root.remove(a)

    added = 0
    if not args.remove:
        for action in ET.parse(snippet_path()).getroot().findall("action"):
            if args.command:
                cmd = action.find("command")
                if cmd.text.startswith("craftdriver "):
                    cmd.text = args.command + cmd.text[len("craftdriver"):]
            root.append(action)
            added += 1

    ET.indent(tree, space="\t")
    uca.parent.mkdir(parents=True, exist_ok=True)
    tmp = uca.with_name(uca.name + ".tmp")
    tree.write(tmp, encoding="UTF-8", xml_declaration=True)
    os.replace(tmp, uca)
    print(f"{uca}: {len(removed)} ação(ões) do CraftDriver removida(s), {added} adicionada(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
