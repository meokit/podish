2026-11-17 experiment source checkpoint, before specialization interface refactor.

Production code baseline: c86246815f8db436c9e5fb3ad57e1621b96d1eb7.
This directory archives authored experiment scripts, generated handler sources,
patches, selected results, and designs. Binary builds, downloaded guest sources,
rootfs fixtures, raw Instruments traces and large profile tables remain ignored.

The directory names mirror artifacts/. To rerun an archived script, restore its
files to artifacts/<directory>/ in this repository; scripts expect those paths
and the previously prepared dependencies/fixtures. Some historical project and
build configuration snapshots contain local absolute paths and need adjusting.
The arm64-regcache prototype.patch captures changes to the copied native source;
its generated files are included separately. The prototype is experimental and
has a known SHA256 throughput regression; it is not enabled in production.
The specialization-interface design is a proposal to implement after this checkpoint.
