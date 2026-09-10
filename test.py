import sys
from pathlib import Path
import unittest

root = Path(__file__).resolve().parent
sys.path.insert(0, str(root / 'backend'))
sys.path.insert(0, str(root / 'tests'))
suite = unittest.defaultTestLoader.discover(str(root / 'tests'))
raise SystemExit(not unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful())
