from pathlib import Path
import re

import pexpect


PROMPT = "/ # "


def _read_anon_pages_kb(child: pexpect.spawn) -> int:
    child.sendline("while read k v u; do [ \"$k\" = \"AnonPages:\" ] && echo $v && break; done < /proc/meminfo")
    child.expect(PROMPT)
    out = child.before or ""
    m = re.search(r"\b(\d+)\s*$", out.strip())
    assert m is not None, f"failed to parse AnonPages from:\n{out}"
    return int(m.group(1))


def test_short_lived_processes_do_not_leak_anon_pages(project_root: Path, alpine_image: str) -> None:
    cmd = [
        "run",
        "--project",
        str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "-i",
        "-t",
        alpine_image,
        "--",
        "/bin/sh",
    ]

    child = pexpect.spawn(
        "dotnet",
        cmd,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=30,
    )

    try:
        child.expect(PROMPT)
        anon_before = _read_anon_pages_kb(child)

        for _ in range(20):
            child.sendline("/bin/true")
            child.expect(PROMPT)

        anon_mid = _read_anon_pages_kb(child)

        for _ in range(20):
            child.sendline("/bin/true")
            child.expect(PROMPT)

        anon_after = _read_anon_pages_kb(child)
        anon_delta_first = anon_mid - anon_before
        anon_delta_second = anon_after - anon_mid
        child.sendline("exit")
        child.expect(pexpect.EOF)
    except pexpect.exceptions.TIMEOUT:
        output = child.before or ""
        child.close(force=True)
        raise AssertionError(f"Command timed out. Partial output:\n{output}")

    output = child.before or ""
    child.close()
    assert child.exitstatus == 0, output
    # Allow one-time cold-start growth (first 20 commands), but second half should
    # not keep growing aggressively if exited children are cleaned up.
    assert anon_delta_second <= 1024, (
        "AnonPages keeps growing in steady state: "
        f"before={anon_before}KiB mid={anon_mid}KiB after={anon_after}KiB "
        f"delta_first={anon_delta_first}KiB delta_second={anon_delta_second}KiB\n"
        f"Output:\n{output}"
    )
