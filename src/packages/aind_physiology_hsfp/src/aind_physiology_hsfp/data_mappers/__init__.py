"""Public offline mapper and command-line entry point."""

import argparse
from pathlib import Path

from ._acquisition import build_acquisition, map_acquisition

__all__ = ["build_acquisition", "map_acquisition"]


def main() -> None:
    parser = argparse.ArgumentParser(description="Map saved HSFP acquisition sources to acquisition_fib.json")
    parser.add_argument("acquisition_root", type=Path)
    args = parser.parse_args()
    print(map_acquisition(args.acquisition_root))
