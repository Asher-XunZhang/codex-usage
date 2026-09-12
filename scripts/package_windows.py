#!/usr/bin/env python3
"""Compatibility entry point; use python -m tools.windows.package for new output defaults."""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from tools.windows import package as _implementation

if __name__ == '__main__':
    _implementation.main(legacy=True)
else:
    # Alias the real module so patches through legacy imports affect its globals.
    sys.modules[__name__] = _implementation
