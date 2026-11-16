// SPDX-License-Identifier: GPL-2.0-or-later
/* Only the main executable's mount/mode determines exec set-ID credentials. */
#define _GNU_SOURCE
#include <sys/statvfs.h>
#include "exec_fixture.h"

static int suid_mounted, nosuid_mounted, scratch_nosuid;
static const struct {
	const char *desc;
	mode_t mode;
	int nosuid, interp;
	unsigned int euid, egid;
} cases[] = {
	{"suid mount honors setuid", 04755, 0, 0, 1001, 1000},
	{"suid mount honors setgid with group execute", 02755, 0, 0, 1000, 1002},
	{"setgid without group execute is ignored", 02745, 0, 0, 1000, 1000},
	{"suid mount honors both bits", 06755, 0, 0, 1001, 1002},
	{"setuid still applies without group execute", 06745, 0, 0, 1001, 1000},
	{"nosuid suppresses setuid", 04755, 1, 0, 1000, 1000},
	{"nosuid suppresses setgid", 02755, 1, 0, 1000, 1000},
	{"nosuid suppresses both bits", 06755, 1, 0, 1000, 1000},
	{"PT_INTERP set-ID bits do not elevate", 0755, 0, 1, 1000, 1000},
	{"nosuid PT_INTERP does not suppress main set-ID", 06755, 0, 1, 1001, 1002},
	{"suid PT_INTERP cannot bypass main nosuid", 06755, 1, 1, 1000, 1000},
};

static void put32(unsigned char **p, uint32_t value)
{
	memcpy(*p, &value, sizeof(value));
	*p += sizeof(value);
}

static void exit_code(unsigned char **p, unsigned int value)
{
	*(*p)++ = 0xb8; put32(p, 1);
	*(*p)++ = 0xbb; put32(p, value);
	*(*p)++ = 0xcd; *(*p)++ = 0x80;
}

static void make_credential_image(const char *path, unsigned int euid,
	unsigned int egid, uint32_t base)
{
	/* i386 raw syscalls, no dynamic loader/libc that could repair credentials.
	 * Check real/effective IDs and AT_SECURE; exit 43 on any mismatch.
	 */
	const unsigned int calls[] = {199, 201, 200, 202};
	const unsigned int values[] = {1000, euid, 1000, egid};
	unsigned char code[256], *p = code;
	const unsigned char find_secure[] = {
		0x8b, 0x0c, 0x24,             /* mov (%esp), %ecx */
		0x8d, 0x74, 0x8c, 0x08,       /* lea 8(%esp,%ecx,4), %esi */
		0xad, 0x85, 0xc0, 0x75, 0xfb, /* skip envp strings */
		0xad,                         /* aux: lodsl (type) */
		0x8b, 0x16,                   /* mov (%esi), %edx (value) */
		0x83, 0xc6, 0x04,
		0x83, 0xf8, AT_SECURE,
		0x74, 0x10,                   /* found: skip loop and failure */
		0x85, 0xc0, 0x75, 0xf1        /* loop until AT_NULL */
	};
	unsigned int i;

	for (i = 0; i < ARRAY_SIZE(calls); i++) {
		*p++ = 0xb8; put32(&p, calls[i]);
		*p++ = 0xcd; *p++ = 0x80;
		*p++ = 0x3d; put32(&p, values[i]); /* cmp expected, eax */
		*p++ = 0x74; *p++ = 12;          /* je next check */
		exit_code(&p, IMAGE_EXIT + 1);
	}
	memcpy(p, find_secure, sizeof(find_secure));
	p += sizeof(find_secure);
	exit_code(&p, IMAGE_EXIT + 1); /* AT_SECURE absent */
	*p++ = 0x81; *p++ = 0xfa;     /* cmp expected, edx */
	put32(&p, euid != 1000 || egid != 1000);
	*p++ = 0x74; *p++ = 12;
	exit_code(&p, IMAGE_EXIT + 1);
	exit_code(&p, IMAGE_EXIT);
	make_image_code(path, NULL, base, code, p - code, 0);
}

static void setup(void)
{
	int isolated = private_mounts();
	struct statvfs fs;

	SAFE_CHMOD(".", 0755);
	SAFE_MKDIR("suidfs", 0755);
	SAFE_MKDIR("nosuidfs", 0755);
	if (isolated) {
		SAFE_MOUNT("exec-creds", "suidfs", "tmpfs", 0, "mode=0755");
		suid_mounted = 1;
		SAFE_MOUNT("exec-creds", "nosuidfs", "tmpfs", MS_NOSUID, "mode=0755");
		nosuid_mounted = 1;
	} else {
		if (statvfs(".", &fs))
			tst_brk(TBROK | TERRNO, "statvfs scratch mount");
		scratch_nosuid = !!(fs.f_flag & ST_NOSUID);
	}
}

static void run(unsigned int i)
{
	const char *path = cases[i].nosuid ? "./nosuidfs/image" : "./suidfs/image";
	const char *ld = cases[i].nosuid ? "./suidfs/ld" : "./nosuidfs/ld";
	char *argv[] = {"image", NULL}, *envp[] = {NULL};

	if ((!nosuid_mounted && (cases[i].nosuid ||
	     (cases[i].interp && (cases[i].mode & 06000)))) || scratch_nosuid) {
		tst_res(TCONF, "%s needs controlled suid/nosuid mounts", cases[i].desc);
		return;
	}
	if (cases[i].interp) {
		make_credential_image(ld, cases[i].euid, cases[i].egid, 0);
		SAFE_CHOWN(ld, 1003, 1004);
		SAFE_CHMOD(ld, 06755);
		make_image(path, ld, MAIN_BASE, 0);
	} else {
		make_credential_image(path, cases[i].euid, cases[i].egid, MAIN_BASE);
	}
	SAFE_CHOWN(path, 1001, 1002);
	SAFE_CHMOD(path, cases[i].mode);
	expect_image(cases[i].desc, run_image(path, argv, envp, 1, 0), 0);
}

static void cleanup(void)
{
	if (nosuid_mounted)
		SAFE_UMOUNT("nosuidfs");
	if (suid_mounted)
		SAFE_UMOUNT("suidfs");
}

static struct tst_test test = {
	.test = run, .tcnt = ARRAY_SIZE(cases), .setup = setup, .cleanup = cleanup,
	.needs_tmpdir = 1, .needs_root = 1, .forks_child = 1,
};
