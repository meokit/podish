/* Independent preflight, not an upstream LTP modification or result. */
#define _GNU_SOURCE
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/statfs.h>
#include <unistd.h>

static void json_string(const char *value)
{
    const unsigned char *p = (const unsigned char *)value;
    putchar('"');
    for (; *p; p++) {
        if (*p == '"' || *p == '\\') {
            putchar('\\');
            putchar(*p);
        } else if (*p < 32) {
            printf("\\u%04x", *p);
        } else {
            putchar(*p);
        }
    }
    putchar('"');
}

int main(void)
{
    char path[] = "/tmp/podish-backend-probe-XXXXXX";
    char mounts[32768];
    struct statfs fs, shmfs;
    int fd = mkstemp(path);
    if (fd < 0 || ftruncate(fd, 4096) || fstatfs(fd, &fs) || statfs("/dev/shm", &shmfs)) {
        perror("backend probe create/stat");
        return 1;
    }
    unsigned char *a = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    unsigned char *b = mmap(NULL, 4096, PROT_READ, MAP_SHARED, fd, 0);
    if (a == MAP_FAILED || b == MAP_FAILED) {
        perror("backend probe mmap");
        return 1;
    }
    a[123] = 0x5a;
    int coherent = b[123] == 0x5a;
    FILE *info = fopen("/proc/self/mountinfo", "r");
    if (!info) {
        perror("backend probe mountinfo");
        return 1;
    }
    size_t count = fread(mounts, 1, sizeof(mounts) - 1, info);
    if (ferror(info) || !feof(info)) {
        fprintf(stderr, "backend probe mountinfo read/truncation\n");
        return 1;
    }
    mounts[count] = 0;
    fclose(info);
    printf("{\"schema\":1,\"dataPath\":");
    json_string(path);
    printf(",\"fsMagic\":%lu,\"shmFsMagic\":%lu,\"sharedMappingCoherent\":%s,\"mountinfo\":",
           (unsigned long)fs.f_type, (unsigned long)shmfs.f_type, coherent ? "true" : "false");
    json_string(mounts);
    puts("}");
    munmap(a, 4096);
    munmap(b, 4096);
    close(fd);
    unlink(path);
    return coherent ? 0 : 1;
}
