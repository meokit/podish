# Developer Notes

This document contains implementation details, design rationale, and subtle behavior notes extracted from the codebase during cleanup.

## Instruction Implementation

### Tail-Call Dispatch
The emulator uses a tail-call dispatch mechanism for executing instructions within a basic block. 
- **`DispatchWrapper`**: Handles the execution of the current instruction handler and then tail-calls the next instruction's handler in the block (`ATTR_MUSTTAIL`).
- **Attributes**: Handlers use `ATTR_PRESERVE_NONE` (Clang-specific) to minimize register saving/restoring overhead, allowing the compiler to use more registers for the emulator state.

### Control Flow (JMP, CALL, RET)
- **JMP/CALL Relative**: The `EIP` in `x86emu` context is updated *after* instruction length is added in `DispatchWrapper`. 
  - For relative jumps (`JMP rel32`), the target is `CurrentEIP + InstructionLength + Rel32`.
  - Since `DispatchWrapper` already advanced `EIP` by `InstructionLength`, the handler only adds `op->imm` (sign-extended displacement).
- **Block Chaining**: `JMP` and `CALL` effectively terminate the current tail-call chain for the standard block execution loop. The main `X86_Run` loop will then look up or decode the next block at the new `EIP`.

### ALU Operations & Flags
- **Parity Flag (PF)**: Calculated based on the least significant byte of the result.
- **Auxiliary Carry Flag (AF)**: Calculated based on carries/borrows between bit 3 and 4.
- **Overflow Flag (OF)**:
  - **ADD**: Set if operands have the same sign but the result has a different sign (signed overflow).
  - **SUB**: Set if operands have different signs and result sign differs from destination (signed overflow).

## Decoder

### Immediate Types (LUT)
The lookup table (`decoder_lut.h`) maps opcodes to immediate types.
- **Correction**: `CALL rel32` (0xE8) and `JMP rel32` (0xE9) must be marked as Type 7 (Dword/Word) to ensure proper 4-byte immediate decoding in 32-bit mode.
- **Correction**: `MOV r/m32, imm32` (0xC7) must be marked as Type 7.
- **Correction**: Short Jumps (0x7x and 0xEB) must be marked as Type 5 (Byte Signed).

## Memory Management (SoftMMU)
- **Zero-Copy Optimization**: The MMU tries to return direct pointers (`GetPtr`) for fast access where possible.
- **IO Hooks**: MMIO handles are provided via `mmio_ops` map (currently skeletal).

