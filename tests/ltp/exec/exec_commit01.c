// SPDX-License-Identifier: GPL-2.0-or-later
/* Linux promises early failure preservation, NOT rollback after mm replacement. */
#define _GNU_SOURCE
#include <signal.h>
#include <sys/ipc.h>
#include <sys/prctl.h>
#include <sys/shm.h>
#include "exec_fixture.h"

static volatile sig_atomic_t handled;
static char alt_memory[65536];

static void handler(int sig)
{
	(void)sig;
	handled++;
}

static void setup(void)
{
	unsigned char invalid[4096] = {0};
	struct rlimit core = {0, 0};
	int fd = SAFE_OPEN("bad-elf", O_CREAT | O_WRONLY, 0755);

	SAFE_SETRLIMIT(RLIMIT_CORE, &core);
	SAFE_WRITE(SAFE_WRITE_ALL, fd, invalid, sizeof(invalid));
	SAFE_CLOSE(fd);
	make_image("bad-interp", "./bad-elf", MAIN_BASE, 0);
	make_image("late-failure", NULL, MAIN_BASE, 1);
}

static void early_failure(const char *path, int expected)
{
	char *argv[] = {"replacement", NULL}, *envp[] = {NULL};
	struct sigaction action = {.sa_handler = handler}, observed;
	stack_t alt = {.ss_sp = alt_memory, .ss_size = sizeof(alt_memory)}, got;
	struct shmid_ds ds;
	shmatt_t nattch_before;
	struct rlimit core = {0, 0};
	char comm[16] = {0};
	int fd, shmid, *anon, *shared;

	SAFE_SETRLIMIT(RLIMIT_CORE, &core);
	fd = SAFE_OPEN("cloexec", O_CREAT | O_RDWR | O_CLOEXEC, 0600);
	sigemptyset(&action.sa_mask);
	SAFE_SIGACTION(SIGUSR1, &action, NULL);
	if (sigaltstack(&alt, NULL) || prctl(PR_SET_NAME, "exec-old-state"))
		tst_brk(TBROK | TERRNO, "prepare pre-exec state");
	anon = SAFE_MMAP(NULL, 4096, PROT_READ | PROT_WRITE,
		MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
	*anon = 0x12345678;
	shmid = shmget(IPC_PRIVATE, 4096, IPC_CREAT | 0600);
	if (shmid < 0)
		tst_brk(TBROK | TERRNO, "shmget");
	shared = shmat(shmid, NULL, 0);
	if (shared == (void *)-1) {
		shmctl(shmid, IPC_RMID, NULL);
		tst_brk(TBROK | TERRNO, "shmat");
	}
	if (shmctl(shmid, IPC_RMID, NULL))
		tst_brk(TBROK | TERRNO, "mark SysV attachment for removal");
	*shared = 0x76543210;
	if (shmctl(shmid, IPC_STAT, &ds))
		tst_brk(TBROK | TERRNO, "IPC_STAT before early failure");
	nattch_before = ds.shm_nattch;
	tst_res(TINFO, "SysV attachment count before exec: %lu", (unsigned long)nattch_before);

	TST_EXP_FAIL(execve(path, argv, envp), expected, "early execve(%s)", path);
	TST_EXP_EQ_LI(fcntl(fd, F_GETFD), FD_CLOEXEC);
	SAFE_SIGACTION(SIGUSR1, NULL, &observed);
	TST_EXP_EQ_LI((observed.sa_handler == handler), 1);
	if (raise(SIGUSR1))
		tst_brk(TBROK | TERRNO, "raise(SIGUSR1)");
	TST_EXP_EQ_LI(handled, 1);
	if (sigaltstack(NULL, &got) || prctl(PR_GET_NAME, comm))
		tst_brk(TBROK | TERRNO, "read post-failure state");
	TST_EXP_EQ_LI((got.ss_sp == alt.ss_sp && got.ss_size == alt.ss_size &&
		!(got.ss_flags & SS_DISABLE)), 1);
	TST_EXP_EQ_LI(strcmp(comm, "exec-old-state"), 0);
	TST_EXP_EQ_LI(*anon, 0x12345678);
	TST_EXP_EQ_LI(*shared, 0x76543210);
	if (shmctl(shmid, IPC_STAT, &ds))
		tst_brk(TBROK | TERRNO, "IPC_STAT after early failure");
	TST_EXP_EQ_LI(ds.shm_nattch, nattch_before);
	if (shmdt(shared))
		tst_brk(TBROK | TERRNO, "shmdt");
	SAFE_MUNMAP(anon, 4096);
	SAFE_CLOSE(fd);
}

static void run(unsigned int i)
{
	char *argv[] = {"late-failure", NULL}, *envp[] = {NULL};
	struct exec_result result;

	if (i < 2) {
		if (!SAFE_FORK()) {
			early_failure(i ? "./bad-interp" : "./bad-elf", i ? ELIBBAD : ENOEXEC);
			_exit(0);
		}
		tst_reap_children();
		return;
	}
	result = run_image("./late-failure", argv, envp, 0, 0);
	tst_res(!result.error && WIFSIGNALED(result.status) &&
		WTERMSIG(result.status) == SIGSEGV ? TPASS : TFAIL,
		"invalid PT_LOAD after point of no return: expected SIGSEGV, "
		"returned errno=%d (%s), wait status=%#x",
		result.error, strerror(result.error), result.status);
}

static struct tst_test test = {
	.test = run, .tcnt = 3, .setup = setup,
	.needs_tmpdir = 1, .forks_child = 1,
};
