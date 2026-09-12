"""Explicit desktop-source and documentation inventory, excluding generated/user data."""
import os
from pathlib import Path

DOCUMENTS = ['README.md', 'LICENSE', 'THIRD-PARTY.md', 'CHANGELOG.md', 'CONTRIBUTING.md']
ROOT_SOURCES = [*DOCUMENTS, 'build.py', 'test.py', 'package.py', 'pytest.ini']
METADATA_FILES = {
    'AGENTS.md',
    '.agents/skills/codex-usage-cross-platform/SKILL.md',
    '.agents/skills/codex-usage-development/SKILL.md',
    '.agents/skills/codex-usage-product-design/SKILL.md',
    '.agents/skills/codex-usage-state-integrity/SKILL.md',
    '.github/workflows/windows.yml', '.github/workflows/docs.yml',
    '.github/ISSUE_TEMPLATE/bug_report.yml', '.github/PULL_REQUEST_TEMPLATE.md',
}
PRIVATE_SUFFIXES = ('.jsonl', '.sqlite', '.sqlite3', '.db', '.csv', '.log', '.pyc', '.pyo', '.pdb')
PRIVATE_NAMES = {'auth.json', 'config.toml', '.ds_store', '.env', 'settings.json', 'worker.json', 'budget.json'}
SOURCE_TREES = {
    'src/backend': {'.py'}, 'src/macos': {'.swift', '.c', '.h'},
    'src/windows': {'.cs', '.csproj', '.xaml', '.manifest', '.ico'},
    'tools': {'.py', '.swift', '.sh'}, 'scripts': {'.py', '.swift', '.sh'},
    'tests': {'.py', '.swift', '.c', '.h'}, 'docs': {'.md', '.png'},
    'integrations': {'.md', '.yaml', '.py'},
}
DOCUMENTATION_MANIFESTS = {'docs/windows/images/1.3.1/capture-manifest.json'}
EXCLUDED_DIRECTORIES = {'__pycache__', 'bin', 'obj', 'node_modules', 'build', 'dist'}


def check_path(name):
    path = Path(name)
    if path.is_absolute() or any(part == '..' or part in EXCLUDED_DIRECTORIES for part in path.parts):
        raise ValueError(f'Unsafe or hidden release path: {name}')
    metadata = path.as_posix().removeprefix('source/') in METADATA_FILES
    if not metadata and any(part.startswith('.') for part in path.parts):
        raise ValueError(f'Unsafe or hidden release path: {name}')
    if name.lower().endswith(PRIVATE_SUFFIXES) or path.name.casefold() in PRIVATE_NAMES:
        raise ValueError(f'Local/private data is not permitted in a release: {name}')


def regular_files(directory):
    """Never traverse generated directories or links in desktop release inputs."""
    directory = Path(directory)
    def reject_link(path):
        if path.is_symlink() or getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
            raise ValueError(f'Symlinks/reparse points are not permitted in the release payload: {path}')
    if not directory.exists():
        if directory.is_symlink():
            reject_link(directory)
        return
    reject_link(directory)
    for current, directories, names in os.walk(directory):
        for name in directories + names:
            reject_link(Path(current) / name)
        directories[:] = sorted(name for name in directories if name not in EXCLUDED_DIRECTORIES and not name.startswith('.'))
        for name in sorted(names):
            path = Path(current) / name
            if path.is_file():
                yield path


def documentation_files(root):
    root = Path(root)
    return [path for path in regular_files(root / 'docs')
            if path.suffix in {'.md', '.png'} or path.relative_to(root).as_posix() in DOCUMENTATION_MANIFESTS]


def source_files(root):
    root = Path(root)
    result = []
    def include(path):
        check_path(path.relative_to(root).as_posix())
        ancestor = root
        for part in path.relative_to(root).parts:
            ancestor /= part
            if ancestor.is_symlink() or (ancestor.exists() and getattr(ancestor.lstat(), 'st_file_attributes', 0) & 0x400):
                raise ValueError(f'Symlink/reparse source path is not permitted: {ancestor}')
        if path.is_symlink() or not path.is_file() or getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
            raise ValueError(f'Missing regular source file: {path}')
        result.append(path)
    for name in ROOT_SOURCES:
        include(root / name)
    for name in sorted(METADATA_FILES):
        include(root / name)
    for directory, suffixes in SOURCE_TREES.items():
        for path in regular_files(root / directory):
            relative = path.relative_to(root).as_posix()
            if path.suffix in suffixes or relative in DOCUMENTATION_MANIFESTS:
                include(path)
    for name in ('resources/macos/runtimes/manifest.json', 'resources/macos/THIRD-PARTY.md',
                 'resources/windows/runtimes/manifest.json'):
        include(root / name)
    for path in regular_files(root / 'resources/macos/third-party-licenses'):
        if path.name == 'LICENSE' or path.name.startswith('LICENSE.') or path.name == 'python-licenses.rst':
            include(path)
        else:
            raise ValueError(f'Unexpected third-party license file: {path}')
    for path in regular_files(root / 'resources/windows/licenses'):
        if path.suffix in {'.txt', '.rtf', '.md'}:
            include(path)
        else:
            raise ValueError(f'Unexpected Windows license file: {path}')
    return result
