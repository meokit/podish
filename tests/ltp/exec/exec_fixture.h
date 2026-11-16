/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef PODISH_EXEC_FIXTURE_H
#define PODISH_EXEC_FIXTURE_H

#include <elf.h>
#include <errno.h>
#include <fcntl.h>
#include <grp.h>
#include <sched.h>
#include <stdint.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/mount.h>
#include <sys/resource.h>
#include <sys/wait.h>
#include <unistd.h>
#include "tst_test.h"

#define IMAGE_EXIT 42
#define MAIN_BASE 0x08048000U

/* No guest libc, dynamic linker, shell, resource binary or host path is needed.
 * The tiny ELF32 payload exits 42. PT_INTERP deliberately uses a relative path:
 * Linux resolves it against cwd, including after the caller drops privileges.
 */
static inline void make_image_code(const char *path, const char *interp,
	uint32_t base, const unsigned char *code, size_t len, int bad_load)
{
	unsigned char data[4096] = {0};
	Elf32_Ehdr eh = {0};
	Elf32_Phdr ph = {0};
	int fd;

	memcpy(eh.e_ident, ELFMAG, SELFMAG);
	eh.e_ident[EI_CLASS] = ELFCLASS32;
	eh.e_ident[EI_DATA] = ELFDATA2LSB;
	eh.e_ident[EI_VERSION] = EV_CURRENT;
	/* Interpreters use a PIC ET_DYN image at base zero, like a real ld.so. */
	eh.e_type = base ? ET_EXEC : ET_DYN;
	eh.e_machine = EM_386;
	eh.e_version = EV_CURRENT;
	eh.e_entry = base + 512;
	eh.e_phoff = sizeof(eh);
	eh.e_ehsize = sizeof(eh);
	eh.e_phentsize = sizeof(ph);
	eh.e_phnum = interp ? 2 : 1;
	memcpy(data, &eh, sizeof(eh));
	if (interp) {
		if (strlen(interp) + 1 > 384)
			tst_brk(TBROK, "PT_INTERP fixture path too long");
		ph.p_type = PT_INTERP;
		ph.p_offset = 128;
		ph.p_filesz = strlen(interp) + 1;
		memcpy(data + sizeof(eh), &ph, sizeof(ph));
		memcpy(data + 128, interp, ph.p_filesz);
	}
	memset(&ph, 0, sizeof(ph));
	ph.p_type = PT_LOAD;
	ph.p_vaddr = base;
	ph.p_filesz = sizeof(data);
	/* Linux checks filesz > memsz after begin_new_exec(): a late failure. */
	ph.p_memsz = bad_load ? 1 : sizeof(data);
	ph.p_flags = PF_R | PF_X;
	ph.p_align = 4096;
	memcpy(data + sizeof(eh) + (interp ? sizeof(ph) : 0), &ph, sizeof(ph));
	if (len > sizeof(data) - 512)
		tst_brk(TBROK, "ELF fixture payload too long");
	memcpy(data + 512, code, len);
	fd = SAFE_OPEN(path, O_CREAT | O_TRUNC | O_WRONLY, 0755);
	SAFE_WRITE(SAFE_WRITE_ALL, fd, data, sizeof(data));
	SAFE_CLOSE(fd);
	SAFE_CHMOD(path, 0755);
}

static inline void make_image(const char *path, const char *interp,
	uint32_t base, int bad_load)
{
	static const unsigned char code[] = {
		0xb8, 1, 0, 0, 0,       /* mov $SYS_exit, %eax */
		0xbb, IMAGE_EXIT, 0, 0, 0, /* mov $42, %ebx */
		0xcd, 0x80
	};
	make_image_code(path, interp, base, code, sizeof(code), bad_load);
}

struct exec_result {
	int error; /* zero on replacement, positive errno on returned execve */
	int status;
};

static inline struct exec_result run_image(const char *path, char *const argv[],
	char *const envp[], int drop_ids, rlim_t stack_limit)
{
	struct exec_result result = {0};
	int pipefd[2];
	pid_t pid;
	ssize_t n;

	SAFE_PIPE(pipefd);
	SAFE_FCNTL(pipefd[1], F_SETFD, FD_CLOEXEC);
	pid = SAFE_FORK();
	if (!pid) {
		int error;
		SAFE_CLOSE(pipefd[0]);
		if (stack_limit) {
			struct rlimit lim;
			SAFE_GETRLIMIT(RLIMIT_STACK, &lim);
			lim.rlim_cur = stack_limit;
			SAFE_SETRLIMIT(RLIMIT_STACK, &lim);
		}
		if (drop_ids) {
			if (setgroups(0, NULL) || setresgid(1000, 1000, 1000) ||
			    setresuid(1000, 1000, 1000))
				tst_brk(TBROK | TERRNO, "drop fixture credentials");
		}
		execve(path, argv, envp);
		error = errno;
		SAFE_WRITE(SAFE_WRITE_ALL, pipefd[1], &error, sizeof(error));
		_exit(0);
	}
	SAFE_CLOSE(pipefd[1]);
	do {
		n = read(pipefd[0], &result.error, sizeof(result.error));
	} while (n < 0 && errno == EINTR);
	if (n != 0 && n != sizeof(result.error))
		tst_brk(TBROK | TERRNO, "read exec result: %ld bytes", (long)n);
	SAFE_CLOSE(pipefd[0]);
	SAFE_WAITPID(pid, &result.status, 0);
	return result;
}

static inline void expect_image(const char *desc, struct exec_result result,
	int error)
{
	int ok = error ? result.error == error && WIFEXITED(result.status) &&
		WEXITSTATUS(result.status) == 0 : !result.error &&
		WIFEXITED(result.status) && WEXITSTATUS(result.status) == IMAGE_EXIT;

	tst_res(ok ? TPASS : TFAIL,
		"%s: expected %s (%d), returned errno=%d (%s), wait status=%#x",
		desc, error ? "errno" : "image exit", error ? error : IMAGE_EXIT,
		result.error, strerror(result.error), result.status);
}

static inline int private_mounts(void)
{
	if (unshare(CLONE_NEWNS)) {
		if (errno == EPERM || errno == ENOSYS) {
			tst_res(TINFO | TERRNO,
				"private mount namespace unavailable; only mount variants will skip");
			return 0;
		}
		tst_brk(TBROK | TERRNO, "unshare(CLONE_NEWNS)");
	}
	/* Never remount or propagate changes into the native host namespace. */
	SAFE_MOUNT(NULL, "/", NULL, MS_REC | MS_PRIVATE, NULL);
	return 1;
}

#endif
