#!/usr/bin/env python3
from __future__ import annotations

import argparse
import functools
import http.server
import socketserver
from pathlib import Path


class CoopCoepHandler(http.server.SimpleHTTPRequestHandler):
    def end_headers(self) -> None:
        self.send_header("Cross-Origin-Opener-Policy", "same-origin")
        self.send_header("Cross-Origin-Embedder-Policy", "require-corp")
        self.send_header("Cross-Origin-Resource-Policy", "cross-origin")
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def guess_type(self, path: str) -> str:
        if path.endswith(".wasm"):
            return "application/wasm"
        return super().guess_type(path)


def main() -> None:
    parser = argparse.ArgumentParser(description="Serve browser-wasm publish output with COOP/COEP headers.")
    # Default search paths for standard .NET Wasm publish layouts
    default_dir = "PodishApp/browserwasm/bin/Release/net10.0/browser-wasm/publish/wwwroot"
    if not Path(default_dir).exists():
        fallback = "PodishApp/browserwasm/bin/Release/net10.0/publish/wwwroot"
        if Path(fallback).exists():
            default_dir = fallback

    parser.add_argument(
        "--dir",
        default=default_dir,
        help="Directory to serve.",
    )
    parser.add_argument("--host", default="127.0.0.1", help="Bind host.")
    parser.add_argument("--port", type=int, default=8081, help="Bind port.")
    args = parser.parse_args()

    directory = Path(args.dir).resolve()
    handler = functools.partial(CoopCoepHandler, directory=str(directory))

    with socketserver.ThreadingTCPServer((args.host, args.port), handler) as httpd:
        print(f"Serving {directory} at http://{args.host}:{args.port}")
        httpd.serve_forever()


if __name__ == "__main__":
    main()
