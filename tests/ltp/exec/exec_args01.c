// SPDX-License-Identifier: GPL-2.0-or-later
/* argv and envp share one string + pointer budget; all large entries are <128K. */
#define _GNU_SOURCE
#include <stdlib.h>
#include "exec_fixture.h"

static char block[4096], empty[] = "";
static const struct {
	const char *desc;
	size_t argc, envc;
	int empty_strings, error;
	rlim_t stack;
} cases[] = {
	{"argv-only 144KiB control", 36, 0, 0, 0, 1024 * 1024},
	{"env-only 144KiB control", 1, 36, 0, 0, 1024 * 1024},
	{"combined 192KiB below 256KiB budget", 24, 24, 0, 0, 1024 * 1024},
	{"argv 144KiB + env 144KiB exceeds combined budget", 36, 36, 0, E2BIG, 1024 * 1024},
	{"argv 192KiB + env 96KiB exceeds combined budget", 48, 24, 0, E2BIG, 1024 * 1024},
	{"argv 96KiB + env 192KiB exceeds combined budget", 24, 48, 0, E2BIG, 1024 * 1024},
	{"combined pointer budget with empty strings", 28000, 28000, 1, E2BIG, 1024 * 1024},
	{"128KiB ARG_MAX floor at 256KiB stack", 12, 12, 0, 0, 256 * 1024},
	{"combined 144KiB exceeds 128KiB floor", 18, 18, 0, E2BIG, 256 * 1024},
};

static void setup(void)
{
	memset(block, 'x', sizeof(block) - 1);
	make_image("image", NULL, MAIN_BASE, 0);
}

static void run(unsigned int i)
{
	char **argv = SAFE_MALLOC((cases[i].argc + 1) * sizeof(char *));
	char **envp = SAFE_MALLOC((cases[i].envc + 1) * sizeof(char *));
	char *s = cases[i].empty_strings ? empty : block;
	size_t n;

	for (n = 0; n < cases[i].argc; n++)
		argv[n] = s;
	argv[n] = NULL;
	for (n = 0; n < cases[i].envc; n++)
		envp[n] = s;
	envp[n] = NULL;
	expect_image(cases[i].desc,
		run_image("./image", argv, envp, 0, cases[i].stack), cases[i].error);
	free(argv);
	free(envp);
}

static struct tst_test test = {
	.test = run, .tcnt = ARRAY_SIZE(cases), .setup = setup,
	.needs_tmpdir = 1, .forks_child = 1,
};
