#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <unistd.h>

#ifndef _LINUX_CAPABILITY_VERSION_3
#define _LINUX_CAPABILITY_VERSION_3 0x20080522
#endif

struct __user_cap_header_struct {
    unsigned int version;
    int pid;
};

struct __user_cap_data_struct {
    unsigned int effective;
    unsigned int permitted;
    unsigned int inheritable;
};

int main() {
    printf("Starting identity tests...\n");

    uid_t ruid, euid, suid;
    gid_t rgid, egid, sgid;

    if (getresuid(&ruid, &euid, &suid) < 0) {
        perror("getresuid");
        return 1;
    }
    printf("UIDs: %d %d %d\n", ruid, euid, suid);

    if (getresgid(&rgid, &egid, &sgid) < 0) {
        perror("getresgid");
        return 1;
    }
    printf("GIDs: %d %d %d\n", rgid, egid, sgid);

    // Test getgroups
    int ngroups = getgroups(0, NULL);
    if (ngroups < 0) {
        perror("getgroups size");
        return 1;
    }
    printf("Num groups: %d\n", ngroups);

    if (ngroups > 0) {
        gid_t* groups = malloc(ngroups * sizeof(gid_t));
        if (getgroups(ngroups, groups) < 0) {
            perror("getgroups list");
            free(groups);
            return 1;
        }
        for (int i = 0; i < ngroups; i++) {
            printf("Group %d: %d\n", i, groups[i]);
        }
        free(groups);
    }

    // Test setgroups (should fail if not root, succeed if root)
    gid_t new_groups[] = {100, 200};
    if (geteuid() == 0) {
        if (setgroups(2, new_groups) < 0) {
            perror("setgroups root");
            return 1;
        }
        printf("setgroups succeeded (root)\n");
    } else {
        if (setgroups(2, new_groups) == 0) {
            fprintf(stderr, "setgroups should have failed for non-root\n");
            return 1;
        } else {
            printf("setgroups failed as expected (non-root): %s\n", strerror(errno));
        }
    }

    // Test capability syscalls (capget)
    struct __user_cap_header_struct hdr = {_LINUX_CAPABILITY_VERSION_3, 0};
    struct __user_cap_data_struct data[2];

    if (syscall(SYS_capget, &hdr, data) < 0) {
        perror("capget");
        // continue, might not be fatal
    } else {
        printf("Caps: %08x %08x\n", data[0].effective, data[1].effective);
    }

    // Test chown/chmod on a temp file
    char filename[] = "test_identity_file.txt";
    int fd = open(filename, O_WRONLY | O_CREAT | O_TRUNC, 0600);
    if (fd < 0) {
        perror("open");
        return 1;
    }
    write(fd, "hello", 5);
    close(fd);

    if (chmod(filename, 0644) < 0) {
        perror("chmod");
        unlink(filename);
        return 1;
    }

    // chown to self should succeed
    if (chown(filename, getuid(), getgid()) < 0) {
        perror("chown self");
        unlink(filename);
        return 1;
    }

    // Test access
    if (access(filename, R_OK) < 0) {
        perror("access R_OK");
        unlink(filename);
        return 1;
    }

    unlink(filename);

    printf("Identity tests passed.\n");
    return 0;
}
