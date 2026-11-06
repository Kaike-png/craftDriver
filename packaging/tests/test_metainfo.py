"""Regressões da geração AppStream e da coerência das versões de release (sem rede)."""
import importlib.util
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

SPEC = importlib.util.spec_from_file_location("metainfo", Path(__file__).resolve().parents[1] / "metainfo.py")
assert SPEC is not None and SPEC.loader is not None
META = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(META)
TEMPLATE = '<component><releases><release version="0.0.1" date="2020-01-01" /></releases></component>'
CHANGELOG = """## [Não lançado]

## [0.2.0] - 2026-11-05
### Novidades
- **TLS** e <certificados> com & sem aspas.
  Continuação com `código`.
- [Documento](https://exemplo.test).
## [0.1.4] - 2026-11-04
- Anterior.
"""


class MetainfoTests(unittest.TestCase):
    def test_gera_todas_as_versoes_notas_e_datas(self):
        xml = META.generated(TEMPLATE, CHANGELOG)
        releases = ET.fromstring(xml).findall("releases/release")
        self.assertEqual([r.attrib for r in releases], [dict(version="0.2.0", date="2026-11-05"), dict(version="0.1.4", date="2026-11-04")])
        self.assertEqual(releases[0].findtext("description/ul/li"), 'TLS e <certificados> com & sem aspas. Continuação com código.')
        self.assertEqual(xml, META.generated(xml, CHANGELOG), "a geração precisa ser determinística e idempotente")

    def test_confere_tag_csproj_metainfo_changelog(self):
        xml = META.generated(TEMPLATE, CHANGELOG)
        csproj = '<Project><PropertyGroup><Version>0.2.0</Version></PropertyGroup></Project>'
        for tag in ("", "v0.2.0", "0.2.0"):
            self.assertEqual(META.check_release(tag, csproj, xml, CHANGELOG), "0.2.0")
        for tag, cs, meta, log in [
            ("v0.2.1", csproj, xml, CHANGELOG),
            ("v0.2.0", csproj.replace("0.2.0", "0.1.4"), xml, CHANGELOG),
            ("v0.2.0", csproj, xml.replace('version="0.2.0"', 'version="0.1.5"'), CHANGELOG),
            ("v0.2.0", csproj, xml, CHANGELOG.replace("2026-11-05", "2026-11-06")),
            ("v0.2.0", csproj, xml, CHANGELOG.replace("[0.2.0]", "[0.2.1]")),
        ]:
            with self.subTest(tag=tag, cs=cs, meta=meta[:150], log=log[:150]), self.assertRaises(ValueError):
                META.check_release(tag, cs, meta, log)

    def test_rejeita_versao_duplicada_data_invalida_e_notas_vazias(self):
        for log in (CHANGELOG + CHANGELOG, CHANGELOG.replace("2026-11-05", "2026-20-05"), "## [0.2.0] - 2026-11-05\n"):
            with self.subTest(log=log), self.assertRaises(ValueError):
                META.generated(TEMPLATE, log)


if __name__ == "__main__":
    unittest.main()
