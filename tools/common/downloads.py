"""Verified atomic downloads shared by both desktop platforms."""
import hashlib
import os
from pathlib import Path
import tempfile
from urllib.request import urlopen


def digest_file(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def verify_runtime(path, entry):
    path = Path(path)
    if path.is_symlink() or not path.is_file():
        raise ValueError(f'Runtime must be a regular file: {path}')
    if path.stat().st_size != entry['bytes'] or digest_file(path) != entry['sha256']:
        raise ValueError(f'Runtime size or SHA-256 mismatch; existing file was left untouched: {path}')


def fetch_runtime(directory, entry, opener=None):
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / entry['file']
    if target.exists() or target.is_symlink():
        verify_runtime(target, entry)
        return target, False
    opener = opener or urlopen
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=directory, prefix='.' + entry['file'] + '.', suffix='.part', delete=False) as output:
            temporary = Path(output.name)
            received = 0
            with opener(entry['url'], timeout=60) as response:
                while True:
                    chunk = response.read(1024 * 1024)
                    if not chunk:
                        break
                    received += len(chunk)
                    if received > entry['bytes']:
                        raise ValueError(f'Runtime download exceeds pinned size: {entry["file"]}')
                    output.write(chunk)
            output.flush()
            os.fsync(output.fileno())
        verify_runtime(temporary, entry)
        temporary.chmod(0o644)
        try:
            # link() is atomic and never replaces an existing target, including a
            # mismatched file concurrently created by a different download.
            os.link(temporary, target)
        except FileExistsError:
            verify_runtime(target, entry)
            return target, False
        return target, True
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
