// SPDX-License-Identifier: GPL-2.0-or-later
/* A VMA holds struct file, and hence a writable open description, after close. */
#define _GNU_SOURCE
#include "exec_fixture.h"

static const struct {
	const char *desc;
	int open_flags, prot, flags, protect, unmap, error;
} cases[] = {
	{"shared writable VMA after close", O_RDWR, PROT_READ | PROT_WRITE, MAP_SHARED, 0, 0, ETXTBSY},
	{"shared read-only VMA from O_RDWR after close", O_RDWR, PROT_READ, MAP_SHARED, 0, 0, ETXTBSY},
	{"private writable VMA from O_RDWR after close", O_RDWR, PROT_READ | PROT_WRITE, MAP_PRIVATE, 0, 0, ETXTBSY},
	{"private read-only VMA from O_RDWR after close", O_RDWR, PROT_READ, MAP_PRIVATE, 0, 0, ETXTBSY},
	{"mprotect read-only does not release writer", O_RDWR, PROT_READ | PROT_WRITE, MAP_SHARED, 1, 0, ETXTBSY},
	{"last munmap releases closed writer", O_RDWR, PROT_READ | PROT_WRITE, MAP_SHARED, 0, 1, 0},
	{"shared VMA from O_RDONLY control", O_RDONLY, PROT_READ, MAP_SHARED, 0, 0, 0},
	{"private writable VMA from O_RDONLY control", O_RDONLY, PROT_READ | PROT_WRITE, MAP_PRIVATE, 0, 0, 0},
};

static void setup(void)
{
	make_image("image", NULL, MAIN_BASE, 0);
}

static void run(unsigned int i)
{
	char *argv[] = {"image", NULL}, *envp[] = {NULL};
	int fd = SAFE_OPEN("image", cases[i].open_flags);
	void *map = SAFE_MMAP(NULL, 4096, cases[i].prot, cases[i].flags, fd, 0);

	SAFE_CLOSE(fd);
	if (cases[i].protect)
		SAFE_MPROTECT(map, 4096, PROT_READ);
	if (cases[i].unmap)
		SAFE_MUNMAP(map, 4096);
	expect_image(cases[i].desc, run_image("./image", argv, envp, 0, 0), cases[i].error);
	if (!cases[i].unmap)
		SAFE_MUNMAP(map, 4096);
	/* A failed exec must not leak deny-write/open references either. */
	expect_image("after final munmap", run_image("./image", argv, envp, 0, 0), 0);
}

static struct tst_test test = {
	.test = run, .tcnt = ARRAY_SIZE(cases), .setup = setup,
	.needs_tmpdir = 1, .forks_child = 1,
};
