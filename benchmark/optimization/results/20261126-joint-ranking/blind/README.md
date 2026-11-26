# Blind workloads for joint ranking

The two new families are Perl and util-linux. Perl matches records and aggregates numeric columns into 97 groups. The util-linux `rev` utility reverses each ASCII input line. Each workload runs twice per timing sample. The adjacent code-generation check runs GNU gzip 1.12 decompression four times per sample. O1 and O3 share a source tree, input, output, target CPU, C library target and compiler version. Both belong to the training family `gzip`. The training rootfs carries the Debian build of gzip 1.12-1.

`workloads.json` contains every command, guest directory, repetition count and expected output SHA-256. `preregistered.json` pins the manifest, inputs, source release, binary hashes and ELF `.text` sizes. Its freeze time precedes execution of the final four gzip binaries. Both `*-verified.json` files record the byte-level output checks on the unbaked libraries. The two initially built gzip variants had identical machine code because Zig mapped both driver optimization levels to Clang cc1 `-O2`. The final builds pass the optimization level directly to cc1 as well.

## Recreate the panel

Use the experiment's Debian 12 rootfs for each ISA. The verified package revisions are perl-base 5.36.0-7+deb12u4 and util-linux 2.38.1-5+deb12u3. On the build host, install Zig 0.15.2 (Clang 20.1.8), GNU make and Python 3. Input generation used CPython 3.14.6 and zlib 1.2.12. Run these commands from this directory after setting `I386_ROOTFS` and `AARCH64_ROOTFS` to the guest trees.

```sh
work=$(mktemp -d)
curl -fL https://mirrors.kernel.org/gnu/gzip/gzip-1.12.tar.xz -o "$work/gzip-1.12.tar.xz"
python3 prepare_inputs.py "$work/inputs"
for guest in i386 aarch64; do
    ./build-gzip.sh "$work" "$guest"
    case "$guest" in
        i386) rootfs=$I386_ROOTFS ;;
        aarch64) rootfs=$AARCH64_ROOTFS ;;
    esac
    dst="$rootfs/opt/fiber-joint-blind-v1"
    mkdir -p "$dst"
    cp "$work"/inputs/records.* "$dst/"
    cp "$work/bin/$guest/gzip-O1" "$work/bin/$guest/gzip-O3" "$dst/"
done
```

On Linux, set `MAKE=make` if GNU make has that name. The build script verifies the upstream release before unpacking. For each rebuilt ELF, use `llvm-objcopy --dump-section .text=code.bin INPUT /dev/null` and compare the SHA-256 of `code.bin` against the registered hash. Full-file hashes also cover debug data, including paths. The original build directory was `/tmp/fiber-joint-blind` on macOS. The generator verifies the fixed manifest and both input hashes before installing the data. Pass `workloads.json` to the benchmark driver after staging.
