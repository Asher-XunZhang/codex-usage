#!/usr/bin/env python3
"""Compatibility entry for tools.macos.render_previews; retains legacy command defaults."""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from tools.macos import render_previews as _implementation

if __name__ == '__main__':
    raise SystemExit(_implementation.main(legacy=True))
else:
    sys.modules[__name__] = _implementation
