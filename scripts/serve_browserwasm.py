#!/usr/bin/env python3
from __future__ import annotations

import argparse
import functools
import http.server
import socketserver
import os
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

    def send_head(self):
        path = self.translate_path(self.path)
        if os.path.isdir(path):
            return super().send_head()

        if not os.path.exists(path):
            return self.send_error(404, "File not found")

        if not os.path.isfile(path):
            return self.send_error(403, "Forbidden")

        file_size = os.path.getsize(path)
        range_header = self.headers.get("Range")

        if range_header is None:
            return super().send_head()

        import re
        m = re.match(r"bytes=(\d+)-(\d*)", range_header)
        if m is None:
            return self.send_error(400, "Invalid Range header")

        start = int(m.group(1))
        end = m.group(2)

        if start >= file_size:
            self.send_error(416, "Requested Range Not Satisfiable")
            return None

        if end:
            end = int(end)
            if end >= file_size:
                end = file_size - 1
        else:
            end = file_size - 1

        if end < start:
            self.send_error(416, "Requested Range Not Satisfiable")
            return None

        self.send_response(206)
        ctype = self.guess_type(path)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Range", f"bytes {start}-{end}/{file_size}")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(end - start + 1))
        self.send_header("Last-Modified", self.date_time_string(os.path.getmtime(path)))
        self.end_headers()

        f = open(path, "rb")
        f.seek(start)
        self.range = (start, end)
        return f

    def copyfile(self, source, outputfile):
        if hasattr(self, "range"):
            start, end = self.range
            remaining = end - start + 1
            bufsize = 64 * 1024
            while remaining > 0:
                read = source.read(min(bufsize, remaining))
                if not read:
                    break
                outputfile.write(read)
                remaining -= len(read)
            return

        super().copyfile(source, outputfile)


def main() -> None:
    parser = argparse.ArgumentParser(description="Serve browser-wasm publish output with COOP/COEP headers.")
    # Default search paths for standard .NET Wasm publish layouts
    default_dir = "Podish.Browser/bin/Release/net10.0/publish/wwwroot"
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
