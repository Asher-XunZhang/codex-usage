"""Check local documentation links and images in a checkout or desktop source archive."""
import argparse
from pathlib import Path
import re
from urllib.parse import unquote, urlsplit

from tools.common.paths import ROOT


def check_documents(root: Path = ROOT) -> dict:
    root = Path(root).resolve()
    documents = [root / name for name in ('README.md', 'CONTRIBUTING.md', 'AGENTS.md', 'THIRD-PARTY.md')]
    documents += sorted((root / 'docs').rglob('*.md'))
    documents += sorted((root / '.agents/skills').rglob('SKILL.md'))
    checked = 0
    failures = []
    for document in documents:
        if not document.is_file():
            failures.append(f'Missing documentation: {document.relative_to(root)}')
            continue
        markup = document.read_text(encoding='utf-8-sig')
        references = re.findall(r'\]\(\s*(<[^>]+>|[^\s)]+)', markup)
        references += re.findall(r'(?:href|src)=[\"\']([^\"\']+)', markup)
        for reference in references:
            url = urlsplit(reference.strip('<>'))
            if url.scheme or url.netloc or not url.path:
                continue
            target = (document.parent / unquote(url.path)).resolve()
            checked += 1
            if not target.is_relative_to(root) or not target.exists():
                failures.append(f'{document.relative_to(root)} -> {reference}')
    if failures:
        raise ValueError('Broken or escaping documentation links:\n' + '\n'.join(failures))
    return {'documents': len(documents), 'local_links': checked}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT)
    args = parser.parse_args()
    result = check_documents(args.root)
    print(f"Checked {result['documents']} documents and {result['local_links']} local links/assets.")


if __name__ == '__main__':
    main()
