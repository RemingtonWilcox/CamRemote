"""Writes the notices file for a download: NOTICES.md, then the open-source notices that come with Sony's SDK."""
import sys
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parent.parent
sdk = root / "vendor" / "CrSDK"
output = Path(sys.argv[1])

text = (root / "NOTICES.md").read_text(encoding="utf-8")
readme = (sdk / "RemoteCli" / "README.md").read_text(encoding="utf-8")
marker = "## copyright notice and disclaimer for OSS"
if marker not in readme:
    sys.exit("Couldn't find the open-source notices in Sony's README.")
text += "\n" + readme[readme.index(marker):]

with zipfile.ZipFile(sdk / "SourceCodeOfOpenSourceSoftware" / "libusb.zip") as z:
    copying = next((n for n in z.namelist() if n.endswith("COPYING")), None)
    if copying is None:
        sys.exit("Couldn't find libusb's licence in Sony's SDK.")
    text += "\n\n## libusb licence\n\n" + z.read(copying).decode("utf-8")

output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(text, encoding="utf-8")
print(f"wrote {output}")
