FROM ubuntu:24.04 AS builder

ARG QEMU_CONFIGURE_EXTRA=--enable-tcg-interpreter

RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y \
    wget xz-utils build-essential python3 python3-venv ninja-build pkg-config \
    libglib2.0-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
RUN wget -q https://download.qemu.org/qemu-9.0.0.tar.xz && \
    tar xf qemu-9.0.0.tar.xz

WORKDIR /src/qemu-9.0.0/build
RUN ../configure --target-list=i386-linux-user \
    ${QEMU_CONFIGURE_EXTRA} \
    --disable-system \
    --disable-docs \
    --disable-guest-agent \
    --disable-tools \
    --static
RUN make -j$(nproc)

FROM alpine:3.23
COPY --from=builder /src/qemu-9.0.0/build/qemu-i386 /usr/local/bin/qemu-i386
WORKDIR /
ENTRYPOINT ["/usr/local/bin/qemu-i386", "-L", "/rootfs"]
CMD ["/rootfs/coremark/coremark.exe", "0x0", "0x0", "0x66", "30000"]
