import pytest

from tests.runner import Runner, X86EmuBackend


def compile_asm(asm: str) -> bytes:
    return Runner().compile(asm)


@pytest.mark.sse
def test_movdqa_unaligned_load_faults_with_gp():
    emu = X86EmuBackend()
    code = compile_asm(
        """
        movdqa xmm0, [edi]
        hlt
        """
    )

    emu.mem_map(0x1000, 0x1000, 7)
    emu.mem_map(0x2000, 0x1000, 3)
    emu.mem_write(0x1000, code)
    emu.mem_write(0x2104, bytes(range(16)))
    emu.reg_write("EDI", 0x2104)
    emu.start(0x1000, 0x1000 + len(code))

    assert emu.get_status() == 2, "unaligned MOVDQA load should fault"
    assert emu.get_fault_info() == (13, "Vector 13")
    assert emu.reg_read("EIP") == 0x1000


@pytest.mark.sse
def test_movdqa_unaligned_store_faults_with_gp():
    emu = X86EmuBackend()
    code = compile_asm(
        """
        movdqa [edi], xmm0
        hlt
        """
    )

    emu.mem_map(0x1000, 0x1000, 7)
    emu.mem_map(0x2000, 0x1000, 3)
    emu.mem_write(0x1000, code)
    emu.reg_write("EDI", 0x2104)
    emu.reg_write("XMM0", 0x00112233445566778899AABBCCDDEEFF)
    emu.start(0x1000, 0x1000 + len(code))

    assert emu.get_status() == 2, "unaligned MOVDQA store should fault"
    assert emu.get_fault_info() == (13, "Vector 13")
    assert emu.reg_read("EIP") == 0x1000
    assert emu.mem_read(0x2104, 16) == b"\x00" * 16


@pytest.mark.sse
def test_movdqu_unaligned_load_still_works():
    runner = Runner()
    assert runner.run_test(
        name="movdqu unaligned load",
        asm="""
        movdqu xmm1, [edi]
        hlt
        """,
        initial_regs={"EDI": 0x2104},
        expected_regs={"XMM1": 0x00112233445566778899AABBCCDDEEFF},
        expected_read={0x2104: 0x00112233445566778899AABBCCDDEEFF},
    )


@pytest.mark.sse
def test_psrldq_uses_imm8_value():
    runner = Runner()
    assert runner.run_test(
        name="psrldq imm8",
        asm="""
        mov eax, 0x33221100
        mov [0x2000], eax
        mov eax, 0x77665544
        mov [0x2004], eax
        mov eax, 0xbbaa9988
        mov [0x2008], eax
        mov eax, 0xffeeddcc
        mov [0x200c], eax
        movdqu xmm0, [0x2000]
        psrldq xmm0, 0xff
        hlt
        """,
        expected_regs={"XMM0": 0},
    )


@pytest.mark.sse
def test_pshufd_reorders_dwords():
    runner = Runner()
    assert runner.run_test(
        name="pshufd reorders dwords",
        asm="""
        mov eax, 0x03020100
        mov [0x2000], eax
        mov eax, 0x07060504
        mov [0x2004], eax
        mov eax, 0x0b0a0908
        mov [0x2008], eax
        mov eax, 0x0f0e0d0c
        mov [0x200c], eax
        movdqu xmm1, [0x2000]
        pshufd xmm0, xmm1, 0x1b
        hlt
        """,
        expected_regs={"XMM0": 0x03020100070605040B0A09080F0E0D0C},
    )


@pytest.mark.sse
def test_pshufhw_reorders_high_words_only():
    runner = Runner()
    assert runner.run_test(
        name="pshufhw reorders high words",
        asm="""
        mov eax, 0x03020100
        mov [0x2000], eax
        mov eax, 0x07060504
        mov [0x2004], eax
        mov eax, 0x0b0a0908
        mov [0x2008], eax
        mov eax, 0x0f0e0d0c
        mov [0x200c], eax
        movdqu xmm1, [0x2000]
        pshufhw xmm0, xmm1, 0x1b
        hlt
        """,
        expected_regs={"XMM0": 0x09080B0A0D0C0F0E0706050403020100},
    )


@pytest.mark.sse
def test_pshuflw_reorders_low_words_only():
    runner = Runner()
    assert runner.run_test(
        name="pshuflw reorders low words",
        asm="""
        mov eax, 0x03020100
        mov [0x2000], eax
        mov eax, 0x07060504
        mov [0x2004], eax
        mov eax, 0x0b0a0908
        mov [0x2008], eax
        mov eax, 0x0f0e0d0c
        mov [0x200c], eax
        movdqu xmm1, [0x2000]
        pshuflw xmm0, xmm1, 0x1b
        hlt
        """,
        expected_regs={"XMM0": 0x0F0E0D0C0B0A09080100030205040706},
    )
