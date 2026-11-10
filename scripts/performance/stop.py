import argparse
from session import stop_owned

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--session", required=True)
    stop_owned(parser.parse_args().session)
