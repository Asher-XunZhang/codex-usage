"""Run every platform's regression tests; unsupported native tests keep their skips."""
import argparse
import json
import sys
from pathlib import Path
import unittest

root = Path(__file__).resolve().parent
sys.path.insert(0, str(root))
from tools.common.paths import BACKEND

sys.path.insert(0, str(BACKEND))


def test_ids(suite):
    for item in suite:
        if isinstance(item, unittest.TestSuite):
            yield from test_ids(item)
        else:
            yield item.id()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--list', action='store_true', help='Print every discovered test ID as JSON without running tests')
    parser.add_argument('--pattern', default='test_*.py', help='Optional discovery filename pattern')
    args = parser.parse_args(argv)
    loader = unittest.TestLoader()
    suite = loader.discover(str(root / 'tests'), pattern=args.pattern, top_level_dir=str(root))
    if loader.errors:
        print('\n'.join(loader.errors), file=sys.stderr)
        return 1
    if args.list:
        ids = list(test_ids(suite))
        if len(ids) != len(set(ids)):
            raise RuntimeError('Discovery returned duplicate test identities')
        print(json.dumps(sorted(ids), indent=2))
        return 0
    return int(not unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful())


if __name__ == '__main__':
    raise SystemExit(main())
