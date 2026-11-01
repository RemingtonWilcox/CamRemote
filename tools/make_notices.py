"""Writes the notices file for a download: NOTICES.md, the open-source notices that come with Sony's SDK, then those for the animal detector."""
import os
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

text += "\n\n## RF-DETR licence\n\n" + (root / "tools" / "Apache-2.0.txt").read_text(encoding="utf-8")

nuget = Path(os.environ.get("NUGET_PACKAGES") or Path.home() / ".nuget" / "packages")
ort = sorted((nuget / "microsoft.ml.onnxruntime").glob("*/ThirdPartyNotices.txt"))
if not ort:
    sys.exit("Couldn't find ONNX Runtime's notices. Build the app once first.")
text += "\n\n## ONNX Runtime licence\n\n" + (ort[-1].parent / "LICENSE").read_text(encoding="utf-8")
text += "\n\n## ONNX Runtime third-party notices\n\n" + ort[-1].read_text(encoding="utf-8")

output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(text, encoding="utf-8")
print(f"wrote {output}")
