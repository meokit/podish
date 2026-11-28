#pragma once

#include <cstdint>
#include <cstring>
#include <string_view>
#include <vector>

namespace fiberish::wasm_jit {

// Minimal zero-dependency WebAssembly binary emitter for single-block JIT modules.
// Targets WebAssembly 2.0 + SIMD128 + tail-call (return_call / return_call_indirect).
enum class ValType : uint8_t {
    Void = 0x40,
    I32 = 0x7F,
    I64 = 0x7E,
    F32 = 0x7D,
    F64 = 0x7C,
    V128 = 0x7B,
    FuncRef = 0x70,
};

struct ByteBuf {
    std::vector<uint8_t> data;

    void Reserve(size_t n) { data.reserve(n); }
    void Clear() { data.clear(); }
    size_t Size() const { return data.size(); }

    void Byte(uint8_t b) { data.push_back(b); }

    void Bytes(const uint8_t* ptr, size_t n) { data.insert(data.end(), ptr, ptr + n); }

    void U32(uint32_t v) {
        do {
            uint8_t b = static_cast<uint8_t>(v & 0x7Fu);
            v >>= 7;
            if (v != 0) b |= 0x80u;
            data.push_back(b);
        } while (v != 0);
    }

    void S32(int32_t v) {
        for (;;) {
            uint8_t b = static_cast<uint8_t>(v & 0x7F);
            v >>= 7;
            const bool done = (v == 0 && (b & 0x40) == 0) || (v == -1 && (b & 0x40) != 0);
            if (!done) b |= 0x80u;
            data.push_back(b);
            if (done) break;
        }
    }

    void S64(int64_t v) {
        for (;;) {
            uint8_t b = static_cast<uint8_t>(v & 0x7F);
            v >>= 7;
            const bool done = (v == 0 && (b & 0x40) == 0) || (v == -1 && (b & 0x40) != 0);
            if (!done) b |= 0x80u;
            data.push_back(b);
            if (done) break;
        }
    }

    void Str(std::string_view s) {
        U32(static_cast<uint32_t>(s.size()));
        Bytes(reinterpret_cast<const uint8_t*>(s.data()), s.size());
    }

    void Section(uint8_t id, const ByteBuf& payload) {
        Byte(id);
        U32(static_cast<uint32_t>(payload.Size()));
        Bytes(payload.data.data(), payload.Size());
    }
};

class WasmCodeEmitter {
public:
    WasmCodeEmitter() { code_.Reserve(512); }

    void Reset(uint32_t num_params) {
        code_.Clear();
        num_params_ = num_params;
        i32_locals_ = 0;
        i64_locals_ = 0;
    }

    // Locals are laid out as: [params: 0..num_params-1], [i32 locals], [i64 locals].
    // Allocate all i32 locals before finalizing, or allocate by fixed layout:
    // We store i32 locals first and i64 locals second; indices for i64 locals are
    // assigned only after all i32 locals are allocated, OR we use separate groups.
    // Even simpler: allow arbitrary interleaved AddLocal(ValType) and RLE-encode
    // them in the Code section header!
    uint32_t AddLocal(ValType type) {
        const uint32_t idx = num_params_ + static_cast<uint32_t>(local_types_.size());
        local_types_.push_back(type);
        return idx;
    }

    void ResetAll(uint32_t num_params) {
        code_.Clear();
        local_types_.clear();
        num_params_ = num_params;
    }

    const ByteBuf& Code() const { return code_; }
    const std::vector<ValType>& LocalTypes() const { return local_types_; }

    // Control flow
    void Unreachable() { code_.Byte(0x00); }
    void Nop() { code_.Byte(0x01); }
    void Block(ValType bt = ValType::Void) {
        code_.Byte(0x02);
        code_.Byte(static_cast<uint8_t>(bt));
    }
    void Loop(ValType bt = ValType::Void) {
        code_.Byte(0x03);
        code_.Byte(static_cast<uint8_t>(bt));
    }
    void If(ValType bt = ValType::Void) {
        code_.Byte(0x04);
        code_.Byte(static_cast<uint8_t>(bt));
    }
    void Else() { code_.Byte(0x05); }
    void End() { code_.Byte(0x0B); }
    void Br(uint32_t depth) {
        code_.Byte(0x0C);
        code_.U32(depth);
    }
    void BrIf(uint32_t depth) {
        code_.Byte(0x0D);
        code_.U32(depth);
    }
    void Return() { code_.Byte(0x0F); }
    void Call(uint32_t func_idx) {
        code_.Byte(0x10);
        code_.U32(func_idx);
    }
    void CallIndirect(uint32_t type_idx, uint32_t table_idx = 0) {
        code_.Byte(0x11);
        code_.U32(type_idx);
        code_.U32(table_idx);
    }
    void ReturnCall(uint32_t func_idx) {
        code_.Byte(0x12);
        code_.U32(func_idx);
    }
    void ReturnCallIndirect(uint32_t type_idx, uint32_t table_idx = 0) {
        code_.Byte(0x13);
        code_.U32(type_idx);
        code_.U32(table_idx);
    }

    // Parametric
    void Drop() { code_.Byte(0x1A); }
    void Select() { code_.Byte(0x1B); }

    // Locals
    void LocalGet(uint32_t idx) {
        code_.Byte(0x20);
        code_.U32(idx);
    }
    void LocalSet(uint32_t idx) {
        code_.Byte(0x21);
        code_.U32(idx);
    }
    void LocalTee(uint32_t idx) {
        code_.Byte(0x22);
        code_.U32(idx);
    }

    // Memory (align is log2(alignment): 0=1B, 1=2B, 2=4B, 3=8B, 4=16B)
    void I32Load(uint32_t offset = 0, uint32_t align = 0) { MemOp(0x28, align, offset); }
    void I64Load(uint32_t offset = 0, uint32_t align = 0) { MemOp(0x29, align, offset); }
    void F32Load(uint32_t offset = 0, uint32_t align = 0) { MemOp(0x2A, align, offset); }
    void F64Load(uint32_t offset = 0, uint32_t align = 0) { MemOp(0x2B, align, offset); }
    void I32Load8S(uint32_t offset = 0) { MemOp(0x2C, 0, offset); }
    void I32Load8U(uint32_t offset = 0) { MemOp(0x2D, 0, offset); }
    void I32Load16S(uint32_t offset = 0, uint32_t align = 0) { MemOp(0x2E, align, offset); }
    void I32Load16U(uint32_t offset = 0, uint32_t align = 0) { MemOp(0x2F, align, offset); }
    void I32Store(uint32_t offset = 0, uint32_t align = 0) { MemOp(0x36, align, offset); }
    void I64Store(uint32_t offset = 0, uint32_t align = 0) { MemOp(0x37, align, offset); }
    void F32Store(uint32_t offset = 0, uint32_t align = 0) { MemOp(0x38, align, offset); }
    void F64Store(uint32_t offset = 0, uint32_t align = 0) { MemOp(0x39, align, offset); }
    void I32Store8(uint32_t offset = 0) { MemOp(0x3A, 0, offset); }
    void I32Store16(uint32_t offset = 0, uint32_t align = 0) { MemOp(0x3B, align, offset); }

    // Constants
    void I32Const(int32_t v) {
        code_.Byte(0x41);
        code_.S32(v);
    }
    void I32ConstU(uint32_t v) { I32Const(static_cast<int32_t>(v)); }
    void I64Const(int64_t v) {
        code_.Byte(0x42);
        code_.S64(v);
    }
    void I64ConstU(uint64_t v) { I64Const(static_cast<int64_t>(v)); }
    void F32Const(float v) {
        code_.Byte(0x43);
        uint8_t bytes[4];
        std::memcpy(bytes, &v, 4);
        code_.Bytes(bytes, 4);
    }
    void F64Const(double v) {
        code_.Byte(0x44);
        uint8_t bytes[8];
        std::memcpy(bytes, &v, 8);
        code_.Bytes(bytes, 8);
    }

    // i32 comparisons
    void I32Eqz() { code_.Byte(0x45); }
    void I32Eq() { code_.Byte(0x46); }
    void I32Ne() { code_.Byte(0x47); }
    void I32LtS() { code_.Byte(0x48); }
    void I32LtU() { code_.Byte(0x49); }
    void I32GtS() { code_.Byte(0x4A); }
    void I32GtU() { code_.Byte(0x4B); }
    void I32LeS() { code_.Byte(0x4C); }
    void I32LeU() { code_.Byte(0x4D); }
    void I32GeS() { code_.Byte(0x4E); }
    void I32GeU() { code_.Byte(0x4F); }

    // i64 comparisons
    void I64Eqz() { code_.Byte(0x50); }
    void I64Eq() { code_.Byte(0x51); }
    void I64Ne() { code_.Byte(0x52); }
    void I64LtS() { code_.Byte(0x53); }
    void I64LtU() { code_.Byte(0x54); }
    void I64GtS() { code_.Byte(0x55); }
    void I64GtU() { code_.Byte(0x56); }
    void I64LeS() { code_.Byte(0x57); }
    void I64LeU() { code_.Byte(0x58); }
    void I64GeS() { code_.Byte(0x59); }
    void I64GeU() { code_.Byte(0x5A); }

    // f32 comparisons
    void F32Eq() { code_.Byte(0x5B); }
    void F32Ne() { code_.Byte(0x5C); }
    void F32Lt() { code_.Byte(0x5D); }
    void F32Gt() { code_.Byte(0x5E); }
    void F32Le() { code_.Byte(0x5F); }
    void F32Ge() { code_.Byte(0x60); }

    // f64 comparisons
    void F64Eq() { code_.Byte(0x61); }
    void F64Ne() { code_.Byte(0x62); }
    void F64Lt() { code_.Byte(0x63); }
    void F64Gt() { code_.Byte(0x64); }
    void F64Le() { code_.Byte(0x65); }
    void F64Ge() { code_.Byte(0x66); }

    // i32 arithmetic & bitwise
    void I32Clz() { code_.Byte(0x67); }
    void I32Ctz() { code_.Byte(0x68); }
    void I32Popcnt() { code_.Byte(0x69); }
    void I32Add() { code_.Byte(0x6A); }
    void I32Sub() { code_.Byte(0x6B); }
    void I32Mul() { code_.Byte(0x6C); }
    void I32And() { code_.Byte(0x71); }
    void I32Or() { code_.Byte(0x72); }
    void I32Xor() { code_.Byte(0x73); }
    void I32Shl() { code_.Byte(0x74); }
    void I32ShrS() { code_.Byte(0x75); }
    void I32ShrU() { code_.Byte(0x76); }
    void I32Rotl() { code_.Byte(0x77); }
    void I32Rotr() { code_.Byte(0x78); }

    // i64 arithmetic & bitwise
    void I64Add() { code_.Byte(0x7C); }
    void I64Sub() { code_.Byte(0x7D); }
    void I64Mul() { code_.Byte(0x7E); }
    void I64And() { code_.Byte(0x83); }
    void I64Or() { code_.Byte(0x84); }
    void I64Xor() { code_.Byte(0x85); }
    void I64Shl() { code_.Byte(0x86); }
    void I64ShrS() { code_.Byte(0x87); }
    void I64ShrU() { code_.Byte(0x88); }

    // f32 arithmetic
    void F32Abs() { code_.Byte(0x8B); }
    void F32Neg() { code_.Byte(0x8C); }
    void F32Nearest() { code_.Byte(0x90); }
    void F32Sqrt() { code_.Byte(0x91); }
    void F32Add() { code_.Byte(0x92); }
    void F32Sub() { code_.Byte(0x93); }
    void F32Mul() { code_.Byte(0x94); }
    void F32Div() { code_.Byte(0x95); }
    void F32Min() { code_.Byte(0x96); }
    void F32Max() { code_.Byte(0x97); }

    // f64 arithmetic
    void F64Abs() { code_.Byte(0x99); }
    void F64Neg() { code_.Byte(0x9A); }
    void F64Nearest() { code_.Byte(0x9E); }
    void F64Sqrt() { code_.Byte(0x9F); }
    void F64Add() { code_.Byte(0xA0); }
    void F64Sub() { code_.Byte(0xA1); }
    void F64Mul() { code_.Byte(0xA2); }
    void F64Div() { code_.Byte(0xA3); }
    void F64Min() { code_.Byte(0xA4); }
    void F64Max() { code_.Byte(0xA5); }

    // Conversions, reinterprets & sign extension
    void I32WrapI64() { code_.Byte(0xA7); }
    void I64ExtendI32S() { code_.Byte(0xAC); }
    void I64ExtendI32U() { code_.Byte(0xAD); }
    void F32ConvertI32S() { code_.Byte(0xB2); }
    void F32DemoteF64() { code_.Byte(0xB6); }
    void F64ConvertI32S() { code_.Byte(0xB7); }
    void F64PromoteF32() { code_.Byte(0xBB); }
    void I32ReinterpretF32() { code_.Byte(0xBC); }
    void I64ReinterpretF64() { code_.Byte(0xBD); }
    void F32ReinterpretI32() { code_.Byte(0xBE); }
    void F64ReinterpretI64() { code_.Byte(0xBF); }
    void I32Extend8S() { code_.Byte(0xC0); }
    void I32Extend16S() { code_.Byte(0xC1); }

    // Saturating float-to-int truncations (0xFC prefix)
    void I32TruncSatF32S() {
        code_.Byte(0xFC);
        code_.U32(0x00);
    }
    void I32TruncSatF64S() {
        code_.Byte(0xFC);
        code_.U32(0x02);
    }

    // SIMD128 (0xFD prefix + LEB128 opcode)
    void V128Load(uint32_t offset = 0, uint32_t align = 0) { SimdMemOp(0x00, align, offset); }
    void V128Load64Splat(uint32_t offset = 0, uint32_t align = 0) { SimdMemOp(0x0A, align, offset); }
    void V128Store(uint32_t offset = 0, uint32_t align = 0) { SimdMemOp(0x0B, align, offset); }
    void V128ConstZero() {
        SimdOp(0x0C);
        static constexpr uint8_t kZero[16] = {};
        code_.Bytes(kZero, 16);
    }
    void I8x16Shuffle(const uint8_t lanes[16]) {
        SimdOp(0x0D);
        code_.Bytes(lanes, 16);
    }
    void I32x4Splat() { SimdOp(0x11); }
    void I64x2Splat() { SimdOp(0x12); }
    void F32x4Splat() { SimdOp(0x13); }
    void F64x2Splat() { SimdOp(0x14); }

    void I32x4ExtractLane(uint8_t lane) { SimdLaneOp(0x1B, lane); }
    void I32x4ReplaceLane(uint8_t lane) { SimdLaneOp(0x1C, lane); }
    void I64x2ExtractLane(uint8_t lane) { SimdLaneOp(0x1D, lane); }
    void I64x2ReplaceLane(uint8_t lane) { SimdLaneOp(0x1E, lane); }
    void F32x4ExtractLane(uint8_t lane) { SimdLaneOp(0x1F, lane); }
    void F32x4ReplaceLane(uint8_t lane) { SimdLaneOp(0x20, lane); }
    void F64x2ExtractLane(uint8_t lane) { SimdLaneOp(0x21, lane); }
    void F64x2ReplaceLane(uint8_t lane) { SimdLaneOp(0x22, lane); }

    void I8x16Eq() { SimdOp(0x23); }
    void I8x16GtS() { SimdOp(0x27); }
    void I16x8Eq() { SimdOp(0x2D); }
    void I16x8GtS() { SimdOp(0x31); }
    void I32x4Eq() { SimdOp(0x37); }
    void I32x4GtS() { SimdOp(0x3B); }

    void V128Not() { SimdOp(0x4D); }
    void V128And() { SimdOp(0x4E); }
    void V128AndNot() { SimdOp(0x4F); }  // a & ~b
    void V128Or() { SimdOp(0x50); }
    void V128Xor() { SimdOp(0x51); }
    void V128Bitselect() { SimdOp(0x52); }

    void V128Load32Lane(uint8_t lane, uint32_t offset = 0, uint32_t align = 0) {
        SimdMemOp(0x56, align, offset);
        code_.Byte(lane);
    }
    void V128Load64Lane(uint8_t lane, uint32_t offset = 0, uint32_t align = 0) {
        SimdMemOp(0x57, align, offset);
        code_.Byte(lane);
    }
    void V128Store32Lane(uint8_t lane, uint32_t offset = 0, uint32_t align = 0) {
        SimdMemOp(0x5A, align, offset);
        code_.Byte(lane);
    }
    void V128Store64Lane(uint8_t lane, uint32_t offset = 0, uint32_t align = 0) {
        SimdMemOp(0x5B, align, offset);
        code_.Byte(lane);
    }
    void V128Load32Zero(uint32_t offset = 0, uint32_t align = 0) { SimdMemOp(0x5C, align, offset); }
    void V128Load64Zero(uint32_t offset = 0, uint32_t align = 0) { SimdMemOp(0x5D, align, offset); }

    void F32x4DemoteF64x2Zero() { SimdOp(0x5E); }
    void F64x2PromoteLowF32x4() { SimdOp(0x5F); }

    void I8x16Bitmask() { SimdOp(0x64); }
    void I8x16Add() { SimdOp(0x6E); }
    void I8x16Sub() { SimdOp(0x71); }
    void I8x16MinU() { SimdOp(0x77); }
    void I8x16MaxU() { SimdOp(0x79); }

    void I16x8Add() { SimdOp(0x8E); }
    void I16x8Sub() { SimdOp(0x91); }

    void I32x4Bitmask() { SimdOp(0xA4); }
    void I32x4Add() { SimdOp(0xAE); }
    void I32x4Sub() { SimdOp(0xB1); }

    void I64x2Bitmask() { SimdOp(0xC4); }
    void I64x2Add() { SimdOp(0xCE); }
    void I64x2Sub() { SimdOp(0xD1); }

    void F32x4Abs() { SimdOp(0xE0); }
    void F32x4Neg() { SimdOp(0xE1); }
    void F32x4Sqrt() { SimdOp(0xE3); }
    void F32x4Add() { SimdOp(0xE4); }
    void F32x4Sub() { SimdOp(0xE5); }
    void F32x4Mul() { SimdOp(0xE6); }
    void F32x4Div() { SimdOp(0xE7); }
    void F32x4Min() { SimdOp(0xE8); }
    void F32x4Max() { SimdOp(0xE9); }
    void F32x4Pmin() { SimdOp(0xEA); }  // (b < a) ? b : a
    void F32x4Pmax() { SimdOp(0xEB); }  // (a < b) ? b : a

    void F64x2Abs() { SimdOp(0xEC); }
    void F64x2Neg() { SimdOp(0xED); }
    void F64x2Sqrt() { SimdOp(0xEF); }
    void F64x2Add() { SimdOp(0xF0); }
    void F64x2Sub() { SimdOp(0xF1); }
    void F64x2Mul() { SimdOp(0xF2); }
    void F64x2Div() { SimdOp(0xF3); }
    void F64x2Min() { SimdOp(0xF4); }
    void F64x2Max() { SimdOp(0xF5); }
    void F64x2Pmin() { SimdOp(0xF6); }  // (b < a) ? b : a
    void F64x2Pmax() { SimdOp(0xF7); }  // (a < b) ? b : a

    void F64x2ConvertLowI32x4S() { SimdOp(0xFE); }

    // Assemble a complete single-function WebAssembly module with standard Route B imports:
    //   import "env" "memory" (memory 1)
    //   import "env" "__indirect_function_table" (table 0 funcref)
    //   import "env" "read_slow" (func 0 (param i32 i32 i32 i32) (result i64))
    //   import "env" "write_slow" (func 1 (param i32 i32 i32 i32 i32) (result i32))
    //   export "f" (func 2 : HandlerFunc)
    std::vector<uint8_t> BuildModule(std::string_view debug_func_name = {}) const {
        ByteBuf out;
        out.Reserve(code_.Size() + 160);

        // Magic + version 1
        static constexpr uint8_t kHeader[8] = {0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00};
        out.Bytes(kHeader, sizeof(kHeader));

        // 1. Type Section (id = 1)
        {
            ByteBuf sec;
            sec.U32(3);  // 3 types
            // Type 0: HandlerFunc (i32, i32, i64, i64, i64, i32, i64) -> (i64)
            sec.Byte(0x60);
            sec.U32(7);
            sec.Byte(static_cast<uint8_t>(ValType::I32));
            sec.Byte(static_cast<uint8_t>(ValType::I32));
            sec.Byte(static_cast<uint8_t>(ValType::I64));
            sec.Byte(static_cast<uint8_t>(ValType::I64));
            sec.Byte(static_cast<uint8_t>(ValType::I64));
            sec.Byte(static_cast<uint8_t>(ValType::I32));
            sec.Byte(static_cast<uint8_t>(ValType::I64));
            sec.U32(1);
            sec.Byte(static_cast<uint8_t>(ValType::I64));

            // Type 1: read_slow (i32, i32, i32, i32) -> (i64)
            sec.Byte(0x60);
            sec.U32(4);
            sec.Byte(static_cast<uint8_t>(ValType::I32));
            sec.Byte(static_cast<uint8_t>(ValType::I32));
            sec.Byte(static_cast<uint8_t>(ValType::I32));
            sec.Byte(static_cast<uint8_t>(ValType::I32));
            sec.U32(1);
            sec.Byte(static_cast<uint8_t>(ValType::I64));

            // Type 2: write_slow (i32, i32, i32, i32, i32) -> (i32)
            sec.Byte(0x60);
            sec.U32(5);
            sec.Byte(static_cast<uint8_t>(ValType::I32));
            sec.Byte(static_cast<uint8_t>(ValType::I32));
            sec.Byte(static_cast<uint8_t>(ValType::I32));
            sec.Byte(static_cast<uint8_t>(ValType::I32));
            sec.Byte(static_cast<uint8_t>(ValType::I32));
            sec.U32(1);
            sec.Byte(static_cast<uint8_t>(ValType::I32));

            out.Section(1, sec);
        }

        // 2. Import Section (id = 2)
        {
            ByteBuf sec;
            sec.U32(4);

            // memory
            sec.Str("env");
            sec.Str("memory");
            sec.Byte(0x02);  // mem
            sec.Byte(0x00);  // min only
            sec.U32(1);

            // __indirect_function_table
            sec.Str("env");
            sec.Str("__indirect_function_table");
            sec.Byte(0x01);  // table
            sec.Byte(static_cast<uint8_t>(ValType::FuncRef));
            sec.Byte(0x00);  // min only
            sec.U32(0);

            // func 0: read_slow (type 1)
            sec.Str("env");
            sec.Str("read_slow");
            sec.Byte(0x00);
            sec.U32(1);

            // func 1: write_slow (type 2)
            sec.Str("env");
            sec.Str("write_slow");
            sec.Byte(0x00);
            sec.U32(2);

            out.Section(2, sec);
        }

        // 3. Function Section (id = 3): 1 defined function (func 2 -> type 0)
        {
            ByteBuf sec;
            sec.U32(1);
            sec.U32(0);
            out.Section(3, sec);
        }

        // 7. Export Section (id = 7): export "f" -> func 2
        {
            ByteBuf sec;
            sec.U32(1);
            sec.Str("f");
            sec.Byte(0x00);  // func
            sec.U32(2);
            out.Section(7, sec);
        }

        // 10. Code Section (id = 10)
        {
            // Run-length encode local_types_
            std::vector<std::pair<uint32_t, ValType>> runs;
            for (ValType t : local_types_) {
                if (!runs.empty() && runs.back().second == t) {
                    runs.back().first++;
                } else {
                    runs.emplace_back(1, t);
                }
            }

            ByteBuf body;
            body.Reserve(code_.Size() + 16);
            body.U32(static_cast<uint32_t>(runs.size()));
            for (const auto& [count, type] : runs) {
                body.U32(count);
                body.Byte(static_cast<uint8_t>(type));
            }
            body.Bytes(code_.data.data(), code_.Size());
            body.Byte(0x0B);  // function end

            ByteBuf sec;
            sec.Reserve(body.Size() + 8);
            sec.U32(1);  // 1 function body
            sec.U32(static_cast<uint32_t>(body.Size()));
            sec.Bytes(body.data.data(), body.Size());
            out.Section(10, sec);
        }

        // Optional Custom "name" Section (id = 0) for --print-wasm-code diagnostics
        if (!debug_func_name.empty()) {
            ByteBuf func_names;
            func_names.U32(1);
            func_names.U32(2);  // func 2
            func_names.Str(debug_func_name);

            ByteBuf sec;
            sec.Str("name");
            sec.Byte(1);  // subsection 1: function names
            sec.U32(static_cast<uint32_t>(func_names.Size()));
            sec.Bytes(func_names.data.data(), func_names.Size());
            out.Section(0, sec);
        }

        return std::move(out.data);
    }

private:
    void MemOp(uint8_t opcode, uint32_t align, uint32_t offset) {
        code_.Byte(opcode);
        code_.U32(align);
        code_.U32(offset);
    }
    void SimdOp(uint32_t opcode) {
        code_.Byte(0xFD);
        code_.U32(opcode);
    }
    void SimdLaneOp(uint32_t opcode, uint8_t lane) {
        code_.Byte(0xFD);
        code_.U32(opcode);
        code_.Byte(lane);
    }
    void SimdMemOp(uint32_t opcode, uint32_t align, uint32_t offset) {
        code_.Byte(0xFD);
        code_.U32(opcode);
        code_.U32(align);
        code_.U32(offset);
    }

    ByteBuf code_;
    std::vector<ValType> local_types_;
    uint32_t num_params_ = 0;
    uint32_t i32_locals_ = 0;
    uint32_t i64_locals_ = 0;
};

}  // namespace fiberish::wasm_jit
