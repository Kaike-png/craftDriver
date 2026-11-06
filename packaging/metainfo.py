#!/usr/bin/env python3
"""Gera <releases> do AppStream a partir do CHANGELOG (fonte única de versões, datas e notas)."""
import argparse
import datetime
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
META = ROOT / "packaging/share/metainfo/io.github.kaike_png.craftdriver.metainfo.xml"


def generated(template, changelog):
    """Preserva os metadados e substitui apenas <releases>, sem depender de módulos externos."""
    versions = list(re.finditer(r"^## \[(\d+\.\d+\.\d+)\] - (\d{4}-\d{2}-\d{2})\s*$", changelog, re.M))
    if not versions:
        raise ValueError("CHANGELOG sem versões datadas")
    releases = ET.Element("releases")
    seen = set()
    for m in versions:
        version, date = m.groups()
        datetime.date.fromisoformat(date)
        if version in seen:
            raise ValueError("Versão duplicada no CHANGELOG: " + version)
        seen.add(version)
        end = re.search(r"^## \[", changelog[m.end():], re.M)
        body = changelog[m.end():m.end() + end.start()] if end else changelog[m.end():]
        release = ET.SubElement(releases, "release", version=version, date=date)
        description = ET.SubElement(release, "description")
        # Notas curtas do CHANGELOG (listas Markdown, inclusive linhas de continuação). Mantém texto puro:
        # nada de HTML/URLs ativos vindo de Markdown dentro do XML do AppStream.
        bullets = re.findall(r"^- (.+(?:\n  .+)*)", body, re.M)
        if not bullets:
            raise ValueError("Seção sem notas: " + version)
        ul = ET.SubElement(description, "ul")
        for text in bullets:
            text = re.sub(r"\[([^]]+)\]\([^)]+\)", r"\1", text)
            text = text.replace("**", "").replace("`", "")
            ET.SubElement(ul, "li").text = " ".join(text.split())
    ET.indent(releases, space="  ", level=1)
    block = ET.tostring(releases, encoding="unicode")
    result, count = re.subn(r"<releases>.*?</releases>", lambda _: block, template, flags=re.S)
    if count != 1:
        raise ValueError("Metainfo precisa ter exatamente um <releases>")
    ET.fromstring(result)
    return result


def check_release(version, csproj, metainfo, changelog):
    """Tag (opcional) ↔ csproj ↔ última release metainfo ↔ seção/data do CHANGELOG."""
    project_version = ET.fromstring(csproj).findtext(".//Version")
    latest = ET.fromstring(metainfo).find("releases/release")
    if latest is None:
        raise ValueError("Metainfo sem releases")
    expected = version.removeprefix("v") if version else project_version
    if not expected or project_version != expected or latest.get("version") != expected:
        raise ValueError(f"Versões divergentes: tag={version or '(sem tag)'} csproj={project_version} metainfo={latest.get('version')}")
    header = re.search(r"^## \[" + re.escape(expected) + r"\] - (\d{4}-\d{2}-\d{2})\s*$", changelog, re.M)
    if header is None or header.group(1) != latest.get("date"):
        raise ValueError("Seção/data do CHANGELOG difere da release metainfo: " + expected)
    datetime.date.fromisoformat(header.group(1))
    return expected


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--check", action="store_true", help="falha se a metainfo versionada está desatualizada")
    p.add_argument("--tag", default="", help="tag a conferir contra a versão (ex.: v0.2.0)")
    p.add_argument("--output", type=Path, help="destino (padrão: metainfo versionada)")
    a = p.parse_args()
    try:
        template, changelog = META.read_text(), (ROOT / "CHANGELOG.md").read_text()
        result = generated(template, changelog)
        if a.check:
            if template != result:
                raise ValueError("Metainfo desatualizada: rode python3 packaging/metainfo.py")
            version = check_release(a.tag, (ROOT / "src/CraftDriver.App/CraftDriver.App.csproj").read_text(), result, changelog)
            print("Release coerente: " + version)
        else:
            (a.output or META).write_text(result)
        return 0
    except (ValueError, ET.ParseError) as ex:
        print("metainfo: " + str(ex), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
