"""Regression packages shared by unittest discovery and pytest."""
import sys

from tools.common.paths import BACKEND

if str(BACKEND) not in sys.path:
    sys.path.insert(0, str(BACKEND))
