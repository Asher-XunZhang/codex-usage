#!/usr/bin/env python3
"""Compatibility entry: macOS packaging with the original build/ and dist/ defaults."""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from tools.macos.package import *
from tools.macos import package as _implementation


def package(label, arch, output_dir=None, root=ROOT):
    root = Path(root)
    return _implementation.package(label, arch, output_dir if output_dir is not None else root / 'dist',
                                   root, build_dir=root / 'build')


def main(argv=None):
    return _implementation.main(argv, legacy=True)


if __name__ == '__main__':
    main()
