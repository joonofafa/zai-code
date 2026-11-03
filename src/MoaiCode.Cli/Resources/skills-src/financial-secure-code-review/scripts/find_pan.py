#!/usr/bin/env python3
"""Find card-number candidates (13-19 digits, Luhn-valid) in files. Read-only, offline.

Usage: python3 find_pan.py <file-or-dir> [...]
Prints  path:line: <masked number>  (first 6 + last 4 kept). Never prints a full number.
Skips binary files, .git, node_modules, build output, and files over 5 MB.

MATE Builder original. License: MIT.
"""
import os
import re
import sys

CANDIDATE = re.compile(r"(?<![\d])(?:\d[ -]?){12,18}\d(?![\d])")
SKIP_DIRS = {".git", "node_modules", "target", "build", "dist", "bin", "obj", ".gradle", ".idea", "__pycache__", ".venv", "venv"}
MAX_BYTES = 5_000_000


def luhn_ok(digits: str) -> bool:
    total = 0
    for i, ch in enumerate(reversed(digits)):
        d = ord(ch) - 48
        if i % 2 == 1:
            d *= 2
            if d > 9:
                d -= 9
        total += d
    return total % 10 == 0


def mask(digits: str) -> str:
    return digits[:6] + "*" * (len(digits) - 10) + digits[-4:]


def scan_file(path: str) -> int:
    try:
        if os.path.getsize(path) > MAX_BYTES:
            return 0
        with open(path, "rb") as f:
            data = f.read()
    except OSError:
        return 0
    if b"\x00" in data[:8192]:
        return 0  # binary
    text = data.decode("utf-8", errors="replace")
    hits = 0
    for lineno, line in enumerate(text.splitlines(), 1):
        for m in CANDIDATE.finditer(line):
            digits = re.sub(r"[ -]", "", m.group(0))
            if 13 <= len(digits) <= 19 and len(set(digits)) > 1 and luhn_ok(digits):
                print(f"{path}:{lineno}: {mask(digits)}")
                hits += 1
    return hits


def walk(root: str):
    if os.path.isfile(root):
        yield root
        return
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in filenames:
            yield os.path.join(dirpath, name)


def main(argv) -> int:
    if len(argv) < 2:
        print(__doc__.strip(), file=sys.stderr)
        return 2
    total = 0
    for root in argv[1:]:
        for path in walk(root):
            total += scan_file(path)
    print(f"-- {total} Luhn-valid candidate(s)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
