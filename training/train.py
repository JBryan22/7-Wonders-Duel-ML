"""Entry point kept at training/train.py (spec A2): same as `uv run sevenwd-train`. See sevenwd/train.py."""
import sys

from sevenwd.train import main

if __name__ == "__main__":
    sys.exit(main())
