import argparse
from session import cleanup_owned

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Delete only labeled resources belonging to this session; retain artifacts.")
    parser.add_argument("--session", required=True)
    cleanup_owned(parser.parse_args().session)
