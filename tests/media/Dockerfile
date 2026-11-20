FROM debian:bookworm-slim AS build
RUN apt-get update && apt-get install -y --no-install-recommends \
    gcc libc6-dev linux-libc-dev libdrm-dev libgbm-dev libpulse-dev libwayland-dev
WORKDIR /src
COPY tests/media/*.c tests/kmsdrm/probe.c tests/kmsdrm/egl-probe.c tests/gles/fbdev.c tests/gles/es3.c ./
COPY graphics/guest/ ./graphics/
COPY spec/wayland/xdg-shell.xml ./
RUN mkdir -p /out/bin /out/graphics && \
    cc -O2 -Wall -Wextra fbdev-probe.c -o /out/bin/podish-fbdev-probe && \
    cc -O2 -Wall -Wextra pulse-probe.c -lpulse-simple -lpulse -o /out/bin/podish-pulse-probe && \
    wayland-scanner client-header xdg-shell.xml xdg-shell-client.h && \
    wayland-scanner private-code xdg-shell.xml xdg-shell.c && \
    cc -O2 -Wall -Wextra wayland-probe.c xdg-shell.c -lwayland-client -o /out/bin/podish-wayland-probe && \
    cc -O2 -fPIC -fvisibility=hidden -shared -Wl,-z,defs -Wl,-soname,libgbm.so.1 \
        graphics/gbm.c -pthread -o /out/graphics/libgbm.so.1 && \
    cc -O2 -fPIC -fvisibility=hidden -shared -Wl,-z,defs -Wl,-soname,libGLESv2.so.2 \
        -I graphics/include graphics/transport.c graphics/gles.generated.c -pthread -o /out/graphics/libGLESv2.so.2 && \
    cc -O2 -fPIC -fvisibility=hidden -shared -Wl,-z,defs -Wl,-soname,libEGL.so.1,-z,nodelete \
        -I graphics/include graphics/egl.c graphics/wayland.c /out/graphics/libGLESv2.so.2 \
        -ldl -pthread -o /out/graphics/libEGL.so.1 && \
    cc -O2 -Wall -Wextra -I /usr/include/libdrm probe.c -ldrm \
        /out/graphics/libgbm.so.1 -o /out/bin/podish-kmsdrm-probe && \
    cc -O2 -Wall -Wextra -I /usr/include/libdrm -I graphics/include egl-probe.c -ldrm \
        /out/graphics/libgbm.so.1 /out/graphics/libEGL.so.1 /out/graphics/libGLESv2.so.2 \
        -o /out/bin/podish-kmsdrm-egl-probe && \
    cc -O2 -Wall -Wextra -I graphics/include fbdev.c \
        /out/graphics/libEGL.so.1 /out/graphics/libGLESv2.so.2 -o /out/bin/podish-gles-fbdev-probe && \
    cc -O2 -Wall -Wextra -I graphics/include es3.c \
        /out/graphics/libEGL.so.1 /out/graphics/libGLESv2.so.2 -o /out/bin/podish-gles3-probe && \
    cc -O2 -Wall -Wextra -DPROBE_EGL -I graphics/include wayland-probe.c xdg-shell.c \
        -lwayland-client -lwayland-egl /out/graphics/libEGL.so.1 /out/graphics/libGLESv2.so.2 \
        -o /out/bin/podish-wayland-egl-probe

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
    libdrm2 libpulse0 libwayland-client0 libwayland-egl1 && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/bin/ /usr/local/bin/
COPY --from=build /out/graphics/ /opt/podish-graphics/
