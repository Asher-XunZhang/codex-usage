"""Render v1.0.3 documentation with real AppKit views and synthetic data only."""
from pathlib import Path
import argparse
import subprocess
import tempfile
from tools.common.paths import ROOT, macos_source


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'build/previews/macos/v1.0.3')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='codex-release-views-') as temporary:
        main = Path(temporary) / 'main.swift'
        main.write_bytes((ROOT / 'tools/macos/preview/release.swift').read_bytes())
        binary = Path(temporary) / 'render'
        subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main),
                        str(macos_source('Capsule.swift')), str(macos_source('CapsulePlacement.swift')),
                        str(macos_source('ArcColorEditor.swift')), '-o', str(binary)], check=True)
        subprocess.run([str(binary), str(output)], check=True)
    print(output)


if __name__ == '__main__':
    main()
