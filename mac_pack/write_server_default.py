#!/usr/bin/env python3
"""Write encrypted server_default.txt (same scheme as Windows deploy tool / SettingsService)."""
from __future__ import annotations

import argparse
import base64
import sys
from pathlib import Path

# Must match Xiaomuocr.Core.Services.SettingsService.ObfuscationKey
OBFUSCATION_KEY = "XiaomuOCR_2026_ServerDefault"


def obfuscate(text: str) -> str:
    key_bytes = OBFUSCATION_KEY.encode("utf-8")
    data = text.encode("utf-8")
    result = bytearray(len(data))
    for i, b in enumerate(data):
        result[i] = b ^ key_bytes[i % len(key_bytes)]
    return base64.b64encode(bytes(result)).decode("ascii")


def main() -> int:
    p = argparse.ArgumentParser(description="Write server_default.txt for Mac publish payload")
    p.add_argument("--url", required=True, help="Production API base URL, e.g. http://host:3390")
    p.add_argument("--out-dir", required=True, help="Directory that becomes AppContext.BaseDirectory (Resources/app)")
    args = p.parse_args()

    url = (args.url or "").strip().rstrip("/")
    if not url:
        print("error: empty url", file=sys.stderr)
        return 2
    if "localhost" in url or "127.0.0.1" in url:
        print(f"error: refusing localhost URL for release default: {url}", file=sys.stderr)
        return 2

    out = Path(args.out_dir)
    out.mkdir(parents=True, exist_ok=True)
    path = out / "server_default.txt"
    path.write_text(obfuscate(url), encoding="ascii")
    print(f"wrote {path} -> {url}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
