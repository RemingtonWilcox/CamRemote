"""Tests for tools/gen_names.py. Run from the repo root: python -m unittest discover -s tests -p "test_*.py" """
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent.parent / "tools" / "gen_names.py"

BASE = """
enum CrDevicePropertyCode : CrInt32u
{
    CrDeviceProperty_Undefined = 0,
    CrDeviceProperty_FocusMode = 0x0100u,
    CrDeviceProperty_Next,
};
"""


def generate(header):
    with tempfile.TemporaryDirectory() as tmp:
        hdr = Path(tmp, "h")
        hdr.mkdir()
        (hdr / "Fake.h").write_text(header, encoding="utf-8")
        out = Path(tmp, "SdkNames.g.cs")
        run = subprocess.run([sys.executable, "-I", str(SCRIPT), str(hdr), str(out)], capture_output=True, text=True)
        if run.returncode:
            raise AssertionError(run.stderr)
        return out.read_text(encoding="utf-8"), run.stderr


class GenNamesTests(unittest.TestCase):
    def test_property_codes_count_on_from_an_explicit_value(self):
        cs, _ = generate(BASE)
        self.assertIn("FocusMode = 0x100,", cs)
        self.assertIn("Next = 0x101,", cs)

    def test_comments_are_ignored(self):
        cs, _ = generate(BASE.replace("CrDeviceProperty_Next,", "// CrDeviceProperty_Old,\n/* CrDeviceProperty_Gone, */ CrDeviceProperty_Next,"))
        self.assertIn("Next = 0x101,", cs)
        self.assertNotIn("Old", cs)
        self.assertNotIn("Gone", cs)

    def test_value_names_are_made_readable(self):
        cs, _ = generate(BASE + "enum CrExposureProgram { CrExposure_M_Manual = 1, CrExposure_P_Auto };")
        self.assertIn('[0x1] = "M Manual",', cs)
        self.assertIn('[0x2] = "P Auto",', cs)

    def test_a_missing_value_enum_is_a_warning_not_a_failure(self):
        _, err = generate(BASE)
        self.assertIn("warning: enum CrWhiteBalanceSetting not found", err)

    def test_errors_and_warnings_are_collected_and_aliases_skipped(self):
        cs, _ = generate(BASE + """
enum CrError { CrError_None = 0, CrError_Connect = 0x8200, CrError_Connect_Unknown = CrError_Connect, CrError_Connect_Next };
enum CrWarning { CrWarning_Unknown = 0x00020000, CrWarning_Connect_Reconnected };
""")
        self.assertIn('[0x8200] = "CrError_Connect",', cs)
        self.assertNotIn("CrError_Connect_Unknown", cs)
        self.assertIn('[0x8201] = "CrError_Connect_Next",', cs)
        self.assertIn('[0x20001] = "CrWarning_Connect_Reconnected",', cs)


if __name__ == "__main__":
    unittest.main()
