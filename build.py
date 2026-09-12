#!/usr/bin/env python3
"""Compatibility entry: macOS build with the original build/ output locations."""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from tools.macos.build import *
from tools.macos import build as _implementation


def main(argv=None):
    return _implementation.main(argv, legacy=True)


if __name__ == '__main__':
    main()
