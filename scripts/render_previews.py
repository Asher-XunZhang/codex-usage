#!/usr/bin/env python3
"""Render synthetic capsule previews offscreen using the current native source."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=ROOT / 'Sources/Capsule.swift')
    parser.add_argument('--output', type=Path, default=ROOT / '.local/previews')
    args = parser.parse_args()
    if sys.platform != 'darwin' or shutil.which('xcrun') is None:
        parser.error('Rendering requires macOS and Apple Command Line Tools.')
    source = args.source.expanduser().resolve()
    fixture = ROOT / 'scripts/preview/main.swift'
    output = args.output.expanduser().resolve()
    if not source.is_file():
        parser.error('--source must name a Capsule.swift source file.')
    output.mkdir(parents=True, exist_ok=True)
    record = {'source_path': str(source), 'synthetic_data_only': True, 'native_glass': False,
              'output_directory': str(output), 'compile_returncode': None, 'render_returncode': None}
    try:
        # The source snapshot, executable and Swift module cache are removed even
        # after a failed compile or render. Only images and diagnostics survive.
        with tempfile.TemporaryDirectory(prefix='codex-usage-preview-') as temporary:
            stage = Path(temporary)
            snapshot = stage / 'Capsule.swift'
            driver = stage / 'main.swift'
            shutil.copy2(source, snapshot)
            shutil.copy2(fixture, driver)
            record['source_sha256'] = hashlib.sha256(snapshot.read_bytes()).hexdigest()
            record['fixture_sha256'] = hashlib.sha256(driver.read_bytes()).hexdigest()
            binary = stage / 'render'
            command = ['xcrun', 'swiftc', '-swift-version', '5', '-Osize', '-target',
                       f'{platform.machine()}-apple-macosx11.0', '-module-cache-path',
                       str(stage / 'module-cache'), str(driver), str(snapshot), '-o', str(binary)]
            compiled = subprocess.run(command, capture_output=True, text=True, timeout=120)
            (output / 'compile.log').write_text(compiled.stdout + compiled.stderr)
            record['compile_returncode'] = compiled.returncode
            if compiled.returncode:
                print(compiled.stderr, file=sys.stderr)
                return compiled.returncode
            rendered = subprocess.run([str(binary), str(output)], capture_output=True, text=True, timeout=30)
            (output / 'render.log').write_text(rendered.stdout + rendered.stderr)
            record['render_returncode'] = rendered.returncode
            print(rendered.stdout, end='')
            if rendered.returncode:
                print(rendered.stderr, file=sys.stderr)
            return rendered.returncode
    except (OSError, subprocess.SubprocessError) as error:
        record['error'] = str(error)
        print(str(error), file=sys.stderr)
        return 1
    finally:
        (output / 'run-result.json').write_text(json.dumps(record, indent=2) + '\n')


if __name__ == '__main__':
    raise SystemExit(main())
