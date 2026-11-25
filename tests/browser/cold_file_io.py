"""Run cold-file scheduling tests against a published Podish browser wwwroot."""

import argparse
import base64
import functools
from http.server import ThreadingHTTPServer
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from threading import Event, Thread
from urllib.parse import urlsplit

from playwright.sync_api import sync_playwright

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(ROOT / "scripts"))

from serve_browserwasm import CoopCoepHandler

PAGE = """
<!doctype html><meta charset="utf-8"><pre></pre>
<script type="module">
import {podishWorker, writeSessionInput} from "/src/worker-client.js";
const result = document.querySelector("pre");
const decoder = new TextDecoder();
podishWorker.onOutput((_, data) => result.textContent += decoder.decode(data));
podishWorker.onControl((data) => {
    if (data[0] === 2 && data.length >= 5)
        result.dataset.exit = new DataView(data.buffer, data.byteOffset).getInt32(1, true);
});
try {
    const image = new URL("/fixture/image.json", location.href);
    const session = JSON.parse(await podishWorker.invoke("StartStoredImageShell", [image.href]));
    if (!session.ok) throw Error(session.error);
    podishWorker.startRunLoop();
    window.advanceGuest = () => writeSessionInput(new TextEncoder().encode("\\n"));
} catch (error) {
    result.dataset.error = error.message;
}
</script>
"""


def write_fixture(directory, guest):
    directories = ["/", "/bin", "/dev", "/proc", "/sys", "/tmp"]
    files = [dict(Path=path, Type=0x4000, Mode=0o755) for path in directories]
    files.append(dict(Path="/bin/sh", Type=0x8000, Mode=0o755, Size=len(guest),
                      InlineData=base64.b64encode(guest).decode()))
    layers = []
    for name in ("slow", "fast"):
        entry = dict(Path=f"/{name}", Type=0x8000, Mode=0o644, Size=4, DataOffset=0, BlobDigest=name)
        (directory / f"{name}.json").write_text(json.dumps([entry, *files] if name == "slow" else [entry]))
        (directory / f"{name}.bin").write_bytes(name.upper().encode())
        layers.append(dict(Digest=name, MediaType="", Size=4, BlobPath=f"{name}.bin", IndexPath=f"{name}.json"))
    image = dict(ImageReference="test", Registry="", Repository="", Tag="", ManifestDigest="",
                 StoreDirectory="", Layers=layers)
    (directory / "image.json").write_text(json.dumps(image))


class ProbeServer(ThreadingHTTPServer):
    request_queue_size = 128


def handler_for(fixture, requested, release):
    class Handler(CoopCoepHandler):
        def translate_path(self, path):
            path = urlsplit(path).path
            if path == "/src/worker-client.js":
                return str(ROOT / "Podish.Browser/frontend/src/worker-client.js")
            if path.startswith("/fixture/"):
                return str(fixture / Path(path).name)
            if path.startswith("/public/"):
                return super().translate_path(path.removeprefix("/public"))
            return super().translate_path(path)

        def do_GET(self):
            path = urlsplit(self.path).path
            if path == "/probe":
                payload = PAGE.encode()
                self.send_response(200)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
                return
            if path == "/fixture/slow.bin":
                requested.set()
                if not release.wait(20):
                    self.send_error(504)
                    return
            try:
                super().do_GET()
            except (BrokenPipeError, ConnectionResetError):
                # A killed guest can cancel the response before its body is sent.
                return

        def log_message(self, format, *args):
            pass

    return Handler


def run_case(browser, options, case):
    with tempfile.TemporaryDirectory() as temp:
        fixture = Path(temp)
        guest_object = fixture / "guest.o"
        guest_elf = fixture / "guest.elf"
        define = [] if case == "read" else [f"-DTEST_{case.upper()}"]
        subprocess.run([options.clang, "--target=i386-linux-gnu", *define, "-c", HERE / "cold_file_guest.S",
                        "-o", guest_object], check=True)
        subprocess.run([options.linker, "-m", "elf_i386", guest_object, "-o", guest_elf], check=True)
        write_fixture(fixture, guest_elf.read_bytes())
        requested = Event()
        release = Event()
        handler = functools.partial(handler_for(fixture, requested, release), directory=options.wwwroot)
        with ProbeServer(("127.0.0.1", 0), handler) as server:
            thread = Thread(target=server.serve_forever, daemon=True)
            thread.start()
            page = browser.new_page()
            try:
                page.goto(f"http://127.0.0.1:{server.server_port}/probe")
                page.wait_for_function("window.advanceGuest || document.querySelector('pre').dataset.error")
                result = page.locator("pre")
                assert not result.get_attribute("data-error"), result.get_attribute("data-error")
                assert requested.wait(10), "the cold file request did not arrive"
                page.evaluate("window.advanceGuest()")
                # Require sibling progress while the cold response is still held by the server.
                page.wait_for_function("document.querySelector('pre').textContent.includes('FAST completed')")
                if case != "kill":
                    release.set()
                page.wait_for_selector("pre[data-exit]", state="attached")
                assert result.get_attribute("data-exit") == "0", result.inner_text()
                assert ("SLOW completed" in result.inner_text()) == (case != "kill"), result.inner_text()
            finally:
                release.set()
                page.close()
                server.shutdown()
                thread.join()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("wwwroot", type=Path)
    parser.add_argument("--chromium", type=Path, help="use an installed Chromium instead of Playwright's download")
    parser.add_argument("--clang", default="clang")
    parser.add_argument("--linker", default="ld.lld")
    options = parser.parse_args()
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(executable_path=options.chromium)
        for case in ("read", "mmap", "pusha", "kill"):
            run_case(browser, options, case)
            print(f"{case}: passed")
        browser.close()


if __name__ == "__main__":
    main()
