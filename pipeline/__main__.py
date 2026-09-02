"""Allow `python -m pipeline <call-dir>`."""
from .cli import main

if __name__ == "__main__":
    raise SystemExit(main())
