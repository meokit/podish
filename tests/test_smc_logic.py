from tests.runner import Runner

def test_smc_direct_nop():
    """
    Same-block SMC should invalidate the current stream and re-execute safely.
    The patched INC becomes a NOP, so EAX must stay unchanged.
    """
    runner = Runner()
    asm = """
        mov byte [0x1009], 0x90
        nop
        nop
        inc eax
    """

    runner.run_test(
        "SMC Direct NOP",
        asm,
        expected_regs={'EAX': 1},
        check_unicorn=False,
    )

def test_smc_cross_block_uaf():
    """
    Cross-block SMC should see the modified target block on the jump.
    """
    runner = Runner()
    asm = """
        mov byte [0x1020], 0x90
        jmp 0x1020
        times 20 nop
        inc eax
        hlt
    """

    runner.run_test(
        "SMC Cross Block",
        asm,
        expected_regs={'EAX': 1},
        check_unicorn=False,
    )

def test_smc_same_block_farther_target():
    """
    Same-block SMC should also work when the modified instruction is not
    immediately adjacent to the write.
    """
    runner = Runner()
    asm = """
        mov byte [0x100a], 0x90
        nop
        nop
        nop
        inc eax
    """

    runner.run_test(
        "SMC Same Block Farther Target",
        asm,
        expected_regs={'EAX': 1},
        check_unicorn=False,
    )


def test_smc_double_patch_same_page():
    """
    Two SMC writes in sequence should not leave the engine stuck in rerun mode.
    Both patched INC instructions become NOPs.
    """
    runner = Runner()
    asm = """
        mov byte [0x1010], 0x90
        mov byte [0x1011], 0x90
        nop
        nop
        inc eax
        inc eax
    """

    runner.run_test(
        "SMC Double Patch Same Page",
        asm,
        expected_regs={'EAX': 1},
        check_unicorn=False,
    )


def test_smc_patched_instruction_runs_once_after_rerun():
    """
    Re-executing the current write must not duplicate earlier side effects.
    The write changes the following INC into a NOP, but the later INC still runs.
    """
    runner = Runner()
    asm = """
        mov byte [0x1009], 0x90
        nop
        nop
        inc eax
        inc eax
    """

    runner.run_test(
        "SMC Patched Instruction Runs Once After Rerun",
        asm,
        expected_regs={'EAX': 2},
        check_unicorn=False,
    )
