# pjdfstest Runner

- Upstream repository: <https://github.com/pjd/pjdfstest>
- Local checkout path: `tests/pjdfstest/upstream/`
- Pinned upstream commit: `tests/pjdfstest/UPSTREAM_COMMIT`
- Runner entrypoint: `Podish.PjdFs/Podish.PjdFs.csproj`

The checkout under `tests/pjdfstest/upstream/` is intentionally ignored by git.
`Podish.PjdFs` clones the upstream repository on demand and checks out the pinned
commit when the local checkout is missing.

The intent is to keep this suite independent from the existing `pytest` test flow
while avoiding a large vendored subtree in the repository.
