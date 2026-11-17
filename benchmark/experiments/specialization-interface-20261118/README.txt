Specialization declaration refactor verification (2026-11-18).
The normal source and maintainer instructions are in libfibercpu/src/instruction_*
and docs/instruction-specialization.txt. These files archive measurement scripts,
pre-refactor registry capture and validated results; binaries/rootfs/logs remain
under artifacts/specialization-refactor-20261117/.
To rerun scripts, restore these files to that artifacts directory in this repo;
they use the prepared rootfs and CLI variants from the earlier CoreMark experiments.
The benchmark commit field identifies the checkpoint; the after variant is the
refactor working tree, identified by its library SHA256 in summary.json. The final
native library is byte-for-byte identical to the measured after variant.
