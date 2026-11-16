// SPDX-License-Identifier: GPL-2.0-or-later
/* PT_INTERP must go through open_exec(), not merely an ELF file read. */
#define _GNU_SOURCE
#include "exec_fixture.h"

static int mounted;
static const struct {
	const char *desc;
	mode_t mode;
	int drop_ids, private_dir, writer, noexec, error;
} cases[] = {
	{"PT_INTERP executable control", 0755, 0, 0, 0, 0, 0},
	{"execute-only PT_INTERP needs no read permission", 0111, 1, 0, 0, 0, 0},
	{"PT_INTERP without any execute bits", 0644, 0, 0, 0, 0, EACCES},
	{"PT_INTERP DAC denies unprivileged caller", 0700, 1, 0, 0, 0, EACCES},
	{"PT_INTERP parent directory search denied", 0755, 1, 1, 0, 0, EACCES},
	{"PT_INTERP writer even with FD_CLOEXEC", 0755, 0, 0, 1, 0, ETXTBSY},
	{"PT_INTERP on noexec mount", 0755, 0, 0, 0, 1, EACCES},
	{"PT_INTERP noexec precedes writer check", 0755, 0, 0, 1, 1, EACCES},
};

static void setup(void)
{
	int isolated = private_mounts();

	SAFE_CHMOD(".", 0755);
	SAFE_MKDIR("loaderfs", 0755);
	if (isolated) {
		SAFE_MOUNT("exec-interp", "loaderfs", "tmpfs", 0, "mode=0755");
		mounted = 1;
	}
	make_image("image", "./loaderfs/ld", MAIN_BASE, 0);
	make_image("loaderfs/ld", NULL, 0, 0);
}

static void run(unsigned int i)
{
	char *argv[] = {"image", NULL}, *envp[] = {NULL};
	int fd = -1;

	if (cases[i].noexec && !mounted) {
		tst_res(TCONF, "%s needs a private mount namespace", cases[i].desc);
		return;
	}
	if (mounted)
		SAFE_MOUNT(NULL, "loaderfs", "tmpfs", MS_REMOUNT |
			(cases[i].noexec ? MS_NOEXEC : 0), NULL);
	SAFE_CHMOD("loaderfs", cases[i].private_dir ? 0700 : 0755);
	SAFE_CHMOD("loaderfs/ld", cases[i].mode);
	if (cases[i].writer)
		fd = SAFE_OPEN("loaderfs/ld", O_WRONLY | O_CLOEXEC);
	expect_image(cases[i].desc,
		run_image("./image", argv, envp, cases[i].drop_ids, 0), cases[i].error);
	if (fd >= 0)
		SAFE_CLOSE(fd);
}

static void cleanup(void)
{
	if (mounted)
		SAFE_UMOUNT("loaderfs");
}

static struct tst_test test = {
	.test = run, .tcnt = ARRAY_SIZE(cases), .setup = setup, .cleanup = cleanup,
	.needs_tmpdir = 1, .needs_root = 1, .forks_child = 1,
};
