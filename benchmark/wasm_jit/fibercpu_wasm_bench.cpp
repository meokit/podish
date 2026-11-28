#include "bindings.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <functional>
#include <limits>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

namespace {

// ============================================================================
// Minimal SHA-256 (for validating guest stderr/stdout checksums like gemm)
// ============================================================================

struct Sha256 {
    uint32_t h[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
    uint8_t buf[64]{};
    size_t buf_len = 0;
    uint64_t total_len = 0;

    static uint32_t Rotr(uint32_t x, uint32_t n) {
        return (x >> n) | (x << (32 - n));
    }

    void Compress(const uint8_t block[64]) {
        static constexpr uint32_t k[64] = {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
        };
        uint32_t w[64];
        for (int i = 0; i < 16; ++i) {
            w[i] = (uint32_t(block[i * 4]) << 24) | (uint32_t(block[i * 4 + 1]) << 16) |
                   (uint32_t(block[i * 4 + 2]) << 8) | uint32_t(block[i * 4 + 3]);
        }
        for (int i = 16; i < 64; ++i) {
            uint32_t s0 = Rotr(w[i - 15], 7) ^ Rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
            uint32_t s1 = Rotr(w[i - 2], 17) ^ Rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        for (int i = 0; i < 64; ++i) {
            uint32_t S1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
            uint32_t ch = (e & f) ^ ((~e) & g);
            uint32_t t1 = hh + S1 + ch + k[i] + w[i];
            uint32_t S0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
            uint32_t maj = (a & b) ^ (a & c) ^ (b & c);
            uint32_t t2 = S0 + maj;
            hh = g;
            g = f;
            f = e;
            e = d + t1;
            d = c;
            c = b;
            b = a;
            a = t1 + t2;
        }
        h[0] += a;
        h[1] += b;
        h[2] += c;
        h[3] += d;
        h[4] += e;
        h[5] += f;
        h[6] += g;
        h[7] += hh;
    }

    void Update(const uint8_t* data, size_t len) {
        total_len += len;
        while (len > 0) {
            size_t take = std::min(len, size_t{64} - buf_len);
            std::memcpy(buf + buf_len, data, take);
            buf_len += take;
            data += take;
            len -= take;
            if (buf_len == 64) {
                Compress(buf);
                buf_len = 0;
            }
        }
    }

    std::string HexDigest() {
        uint64_t bit_len = total_len * 8;
        uint8_t pad = 0x80;
        Update(&pad, 1);
        uint8_t zero = 0;
        while (buf_len != 56)
            Update(&zero, 1);
        uint8_t len_be[8];
        for (int i = 0; i < 8; ++i)
            len_be[i] = static_cast<uint8_t>(bit_len >> (56 - i * 8));
        Update(len_be, 8);
        char out[65];
        for (int i = 0; i < 8; ++i)
            std::snprintf(out + i * 8, 9, "%08x", h[i]);
        out[64] = '\0';
        return std::string(out);
    }
};

// ============================================================================
// Guest Page Allocator
// ============================================================================

struct GuestMem {
    std::unordered_map<uint32_t, std::unique_ptr<std::array<uint8_t, 4096>>> pages;

    uint8_t* MapPage(EmuState* state, uint32_t vaddr, uint8_t perms) {
        vaddr &= ~0xFFFu;
        auto it = pages.find(vaddr);
        if (it == pages.end()) {
            auto page = std::make_unique<std::array<uint8_t, 4096>>();
            page->fill(0);
            uint8_t* ptr = page->data();
            pages.emplace(vaddr, std::move(page));
            if (!X86_MemMapHostPage(state, vaddr, ptr, perms))
                return nullptr;
            return ptr;
        }
        X86_MemProtect(state, vaddr, 4096, perms);
        return it->second->data();
    }

    bool MapRange(EmuState* state, uint32_t start, uint32_t size, uint8_t perms) {
        if (size == 0)
            return true;
        uint64_t p0 = start & ~0xFFFu;
        uint64_t p1 = (uint64_t(start) + size + 0xFFFu) & ~uint64_t{0xFFFu};
        for (uint64_t p = p0; p < p1; p += 4096) {
            if (!MapPage(state, static_cast<uint32_t>(p), perms))
                return false;
        }
        return true;
    }

    void WriteBytes(EmuState* state, uint32_t addr, const void* src, size_t len) {
        const auto* p = static_cast<const uint8_t*>(src);
        while (len > 0) {
            uint32_t page = addr & ~0xFFFu;
            uint32_t off = addr & 0xFFFu;
            size_t chunk = std::min<size_t>(len, 4096 - off);
            auto it = pages.find(page);
            if (it == pages.end()) {
                MapPage(state, page, 3);
                it = pages.find(page);
            }
            std::memcpy(it->second->data() + off, p, chunk);
            addr += static_cast<uint32_t>(chunk);
            p += chunk;
            len -= chunk;
        }
    }

    bool ReadBytes(EmuState* state, uint32_t addr, void* dst, size_t len) {
        auto* p = static_cast<uint8_t*>(dst);
        while (len > 0) {
            uint32_t page = addr & ~0xFFFu;
            uint32_t off = addr & 0xFFFu;
            size_t chunk = std::min<size_t>(len, 4096 - off);
            void* host = X86_ResolvePtrForRead(state, addr);
            if (!host)
                return false;
            std::memcpy(p, host, chunk);
            addr += static_cast<uint32_t>(chunk);
            p += chunk;
            len -= chunk;
        }
        return true;
    }

    std::string ReadCString(EmuState* state, uint32_t addr) {
        std::string s;
        for (size_t i = 0; i < 4096; ++i) {
            uint8_t c = 0;
            if (!ReadBytes(state, addr + static_cast<uint32_t>(i), &c, 1) || c == 0)
                break;
            s.push_back(static_cast<char>(c));
        }
        return s;
    }
};

// ============================================================================
// Built-in Self-Tests (Equivalence, SMC, Invalidate, Pool Exhaustion, Faults)
// ============================================================================

struct CpuSnap {
    uint32_t regs[8];
    uint32_t eip;
    uint32_t eflags;
    std::array<std::array<uint8_t, 16>, 8> xmm;
};

CpuSnap CaptureSnap(EmuState* s) {
    CpuSnap snap{};
    for (int i = 0; i < 8; ++i) {
        snap.regs[i] = X86_RegRead(s, i);
        X86_ReadXMM(s, i, snap.xmm[i].data());
    }
    snap.eip = X86_GetEIP(s);
    snap.eflags = X86_GetEFLAGS(s);
    return snap;
}

bool RunSnippet(const std::vector<uint8_t>& code, const uint32_t init_regs[8], uint32_t init_eflags, bool jit_on,
                CpuSnap& out_snap, std::array<uint8_t, 8192>& out_data, unsigned runs = 2,
                const std::function<void(uint8_t*)>& init_data = {}) {
    X86_SetWasmJitEnabled(jit_on ? 1 : 0);
    X86_SetWasmJitHotThreshold(1);

    EmuState* s = X86_Create();
    GuestMem mem;
    constexpr uint32_t kCode = 0x10000;
    constexpr uint32_t kData = 0x20000;
    constexpr uint32_t kStack = 0x30000;
    uint8_t* code_pg = mem.MapPage(s, kCode, 5);         // RX
    uint8_t* data_pg0 = mem.MapPage(s, kData, 3);        // RW
    uint8_t* data_pg1 = mem.MapPage(s, kData + 4096, 3); // RW (for cross-page tests)
    mem.MapPage(s, kStack, 3);                           // RW
    std::memcpy(code_pg, code.data(), code.size());

    const uint32_t end_eip = kCode + static_cast<uint32_t>(code.size());
    bool stopped = false;
    for (unsigned run = 0; run < runs; ++run) {
        std::memset(data_pg0, 0, 4096);
        std::memset(data_pg1, 0, 4096);
        for (int i = 0; i < 64; ++i) {
            uint32_t v = static_cast<uint32_t>(i * 7 - 100);
            std::memcpy(data_pg0 + i * 4, &v, 4);
        }
        if (init_data) {
            std::array<uint8_t, 8192> buf{};
            std::memcpy(buf.data(), data_pg0, 4096);
            init_data(buf.data());
            std::memcpy(data_pg0, buf.data(), 4096);
            std::memcpy(data_pg1, buf.data() + 4096, 4096);
        }
        for (int i = 0; i < 8; ++i) {
            X86_RegWrite(s, i, init_regs[i]);
            uint8_t z[16]{};
            X86_WriteXMM(s, i, z);
        }
        X86_RegWrite(s, 4, kStack + 0x800); // ESP
        X86_SetEFLAGS(s, init_eflags);
        X86_SetEIP(s, kCode);
        X86_Run(s, end_eip, 1000000);
        stopped = (X86_GetStatus(s) == 1);
        if (!stopped)
            break;
    }
    out_snap = CaptureSnap(s);
    std::memcpy(out_data.data(), data_pg0, 4096);
    std::memcpy(out_data.data() + 4096, data_pg1, 4096);
    X86_Destroy(s);
    return stopped;
}

bool CompareSnippet(const char* name, const std::vector<uint8_t>& code, const uint32_t init_regs[8],
                    uint32_t init_eflags = 0x202, unsigned runs = 2,
                    const std::function<void(uint8_t*)>& init_data = {}) {
    CpuSnap ref{}, jit{};
    std::array<uint8_t, 8192> ref_data{}, jit_data{};
    bool ok_ref = RunSnippet(code, init_regs, init_eflags, false, ref, ref_data, runs, init_data);
    bool ok_jit = RunSnippet(code, init_regs, init_eflags, true, jit, jit_data, runs, init_data);
    if (!ok_ref || !ok_jit) {
        std::fprintf(stderr, "[FAIL] %s: ok_ref=%d ok_jit=%d\n", name, ok_ref, ok_jit);
        return false;
    }
    bool match = (ref.eip == jit.eip) && (ref.eflags == jit.eflags) && (ref_data == jit_data) && (ref.xmm == jit.xmm);
    for (int i = 0; i < 8; ++i)
        match = match && (ref.regs[i] == jit.regs[i]);
    if (!match) {
        std::fprintf(stderr, "[FAIL] %s: mismatch!\n", name);
        std::fprintf(stderr, "  EIP:    ref=%08x jit=%08x\n", ref.eip, jit.eip);
        std::fprintf(stderr, "  EFLAGS: ref=%08x jit=%08x\n", ref.eflags, jit.eflags);
        for (int i = 0; i < 8; ++i) {
            if (ref.regs[i] != jit.regs[i])
                std::fprintf(stderr, "  R%d:     ref=%08x jit=%08x\n", i, ref.regs[i], jit.regs[i]);
            if (ref.xmm[i] != jit.xmm[i]) {
                uint64_t rlo, rhi, jlo, jhi;
                std::memcpy(&rlo, ref.xmm[i].data(), 8);
                std::memcpy(&rhi, ref.xmm[i].data() + 8, 8);
                std::memcpy(&jlo, jit.xmm[i].data(), 8);
                std::memcpy(&jhi, jit.xmm[i].data() + 8, 8);
                std::fprintf(stderr, "  XMM%d:   ref=%016llx_%016llx jit=%016llx_%016llx\n", i,
                             static_cast<unsigned long long>(rhi), static_cast<unsigned long long>(rlo),
                             static_cast<unsigned long long>(jhi), static_cast<unsigned long long>(jlo));
            }
        }
        return false;
    }
    return true;
}

bool TestSmcAndInvalidation() {
    X86_SetWasmJitEnabled(1);
    X86_SetWasmJitHotThreshold(1);
    X86_ResetWasmJitStats();

    EmuState* s = X86_Create();
    GuestMem mem;
    constexpr uint32_t kCode = 0x10000;
    constexpr uint32_t kStack = 0x30000;
    uint8_t* pg = mem.MapPage(s, kCode, 7); // RWX!
    mem.MapPage(s, kStack, 3);

    // Function at 0x10000:
    //   mov eax, 0x11111111  (B8 11 11 11 11)
    //   ret                  (C3)
    // Driver at 0x10020:
    //   call 0x10000         (E8 DB FF FF FF)  -> eax = 0x11111111 (compiles 0x10000)
    //   mov ebx, eax         (89 C3)
    //   call 0x10000         (E8 D4 FF FF FF)  -> runs JIT-compiled 0x10000!
    //   mov dword ptr [0x10001], 0x22222222  (C7 05 01 00 01 00 22 22 22 22) -> SMC!
    //   call 0x10000         (E8 C5 FF FF FF)  -> MUST see patched 0x22222222!
    //   nop                  (90)
    const uint8_t fn[] = {0xB8, 0x11, 0x11, 0x11, 0x11, 0xC3};
    std::memcpy(pg, fn, sizeof(fn));
    const uint8_t drv[] = {
        0xE8, 0xDB, 0xFF, 0xFF, 0xFF,                               // call 0x10000 (next=0x10025, rel=-0x25=0xFFFFFFDB)
        0x89, 0xC3,                                                 // mov ebx, eax
        0xE8, 0xD4, 0xFF, 0xFF, 0xFF,                               // call 0x10000 (next=0x1002C, rel=-0x2C=0xFFFFFFD4)
        0xC7, 0x05, 0x01, 0x00, 0x01, 0x00, 0x22, 0x22, 0x22, 0x22, // mov [0x10001], 0x22222222 (len 10 -> 0x10036)
        0xE8, 0xC5, 0xFF, 0xFF, 0xFF,                               // call 0x10000 (next=0x1003B, rel=-0x3B=0xFFFFFFC5)
        0x90,                                                       // nop (0x1003C)
    };
    std::memcpy(pg + 0x20, drv, sizeof(drv));

    X86_RegWrite(s, 4, kStack + 0x800);
    X86_SetEIP(s, kCode + 0x20);
    X86_Run(s, kCode + 0x20 + sizeof(drv), 100000);
    uint32_t ebx = X86_RegRead(s, 3);
    uint32_t eax = X86_RegRead(s, 0);
    if (ebx != 0x11111111u || eax != 0x22222222u) {
        std::fprintf(stderr, "[FAIL] TestSMC: ebx=%08x (exp 11111111) eax=%08x (exp 22222222)\n", ebx, eax);
        X86_Destroy(s);
        return false;
    }

    // Now test external patch + X86_ResetCodeCacheByRange
    uint32_t v3 = 0x33333333u;
    std::memcpy(pg + 1, &v3, 4);
    X86_ResetCodeCacheByRange(s, kCode, 4096);
    X86_RegWrite(s, 4, kStack + 0x800);
    X86_SetEIP(s, kCode);
    // Push return sentinel 0x10040
    uint32_t ret_addr = kCode + 0x40;
    mem.WriteBytes(s, kStack + 0x7FC, &ret_addr, 4);
    X86_RegWrite(s, 4, kStack + 0x7FC);
    X86_Run(s, ret_addr, 100000);
    if (X86_RegRead(s, 0) != 0x33333333u) {
        std::fprintf(stderr, "[FAIL] TestResetRange: eax=%08x (exp 33333333)\n", X86_RegRead(s, 0));
        X86_Destroy(s);
        return false;
    }
    X86_Destroy(s);
    return true;
}

bool TestPoolExhaustion() {
    X86_SetWasmJitEnabled(1);
    X86_SetWasmJitHotThreshold(1);
    X86_ResetWasmJitPool();
    X86_ResetWasmJitStats();

    EmuState* s = X86_Create();
    GuestMem mem;
    constexpr uint32_t kBase = 0x10000;
    constexpr uint32_t kStack = 0x80000;
    mem.MapPage(s, kStack, 3);

    // Generate 1100 distinct basic blocks across pages: each does `add eax, 1; jmp next_block`
    // With default pool size 1024, this forces >70 evictions and then re-executes block 0!
    constexpr int kBlocks = 1100;
    for (int i = 0; i < kBlocks; ++i) {
        uint32_t addr = kBase + i * 16;
        uint8_t* pg = mem.MapPage(s, addr & ~0xFFFu, 5);
        uint32_t off = addr & 0xFFFu;
        // add eax, 1 (83 C0 01); jmp +11 (EB 0B) -> lands at addr + 16!
        pg[off + 0] = 0x83;
        pg[off + 1] = 0xC0;
        pg[off + 2] = 0x01;
        if (i + 1 < kBlocks) {
            pg[off + 3] = 0xEB;
            pg[off + 4] = 0x0B; // next = off+5+11 = off+16
        } else {
            // Last block: dec ecx; jnz kBase (0F 85 rel32)
            pg[off + 3] = 0x49; // dec ecx
            pg[off + 4] = 0x0F;
            pg[off + 5] = 0x85;
            int32_t rel = static_cast<int32_t>(kBase) - static_cast<int32_t>(addr + 10);
            std::memcpy(pg + off + 6, &rel, 4);
            pg[off + 10] = 0x90;
        }
    }

    X86_RegWrite(s, 0, 0); // EAX = 0
    X86_RegWrite(s, 1, 3); // ECX = 3 passes (forces compile + evict + re-entry!)
    X86_RegWrite(s, 4, kStack + 0x800);
    X86_SetEIP(s, kBase);
    const uint32_t end_eip = kBase + (kBlocks - 1) * 16 + 10;
    X86_Run(s, end_eip, 10000000);

    const uint32_t eax = X86_RegRead(s, 0);
    if (eax != static_cast<uint32_t>(kBlocks * 3)) {
        std::fprintf(stderr, "[FAIL] TestPoolExhaustion: eax=%u (exp %u)\n", eax, kBlocks * 3);
        X86_Destroy(s);
        return false;
    }
    X86_Destroy(s);
    return true;
}

struct DemandFaultCtx {
    GuestMem* mem;
    uint32_t allow_page;
    int faults_handled = 0;
};

int DemandFaultCallback(EmuState* state, uint32_t addr, int access, void* userdata) {
    auto* ctx = static_cast<DemandFaultCtx*>(userdata);
    if ((addr & ~0xFFFu) == ctx->allow_page) {
        uint8_t* p = ctx->mem->MapPage(state, ctx->allow_page, 3);
        uint32_t init_val = 0x12345678u;
        std::memcpy(p, &init_val, 4);
        ctx->faults_handled++;
        return 1;
    }
    return 0;
}

bool TestDemandFaultAndAbort() {
    X86_SetWasmJitEnabled(1);
    X86_SetWasmJitHotThreshold(1);

    EmuState* s = X86_Create();
    GuestMem mem;
    constexpr uint32_t kCode = 0x10000;
    constexpr uint32_t kGoodPage = 0x40000;
    constexpr uint32_t kLazyPage = 0x50000;
    constexpr uint32_t kBadPage = 0x60000;
    uint8_t* code = mem.MapPage(s, kCode, 5);
    uint8_t* good = mem.MapPage(s, kGoodPage, 3);
    uint32_t v0 = 0x0000000Au;
    std::memcpy(good, &v0, 4);

    // Block at kCode:
    //   add eax, 1           (83 C0 01)
    //   mov edx, [ebx]       (8B 13)       <- may fault!
    //   add edx, eax         (01 C2)
    //   mov [ebx + 4], edx   (89 53 04)
    //   nop                  (90)
    const uint8_t prog[] = {0x83, 0xC0, 0x01, 0x8B, 0x13, 0x01, 0xC2, 0x89, 0x53, 0x04, 0x90};
    std::memcpy(code, prog, sizeof(prog));

    DemandFaultCtx fctx{&mem, kLazyPage, 0};
    X86_SetExtendedFaultCallback(s, DemandFaultCallback, &fctx);

    // 1. Warm up on kGoodPage so block compiles to Wasm JIT!
    for (int i = 0; i < 3; ++i) {
        X86_RegWrite(s, 0, 5);
        X86_RegWrite(s, 3, kGoodPage);
        X86_SetEIP(s, kCode);
        X86_Run(s, kCode + sizeof(prog), 10000);
    }

    // 2. Run on kLazyPage (unmapped -> demand-paged by fault callback inside JIT!)
    X86_RegWrite(s, 0, 7);
    X86_RegWrite(s, 3, kLazyPage);
    X86_SetEIP(s, kCode);
    X86_Run(s, kCode + sizeof(prog), 10000);
    uint32_t written = 0;
    mem.ReadBytes(s, kLazyPage + 4, &written, 4);
    if (fctx.faults_handled != 1 || written != 0x12345680u) {
        std::fprintf(stderr, "[FAIL] TestDemandFault: handled=%d written=%08x (exp 12345680)\n", fctx.faults_handled,
                     written);
        X86_Destroy(s);
        return false;
    }

    // 3. Run on kBadPage (unhandled fault -> exact EIP = kCode + 3, fault_vector = 14, EAX = 100)
    X86_RegWrite(s, 0, 99);
    X86_RegWrite(s, 3, kBadPage);
    X86_SetEIP(s, kCode);
    X86_Run(s, kCode + sizeof(prog), 10000);
    if (X86_GetStatus(s) != 2 || X86_GetEIP(s) != kCode + 3 || X86_GetFaultVector(s) != 14 ||
        X86_GetFaultAddress(s) != kBadPage || X86_RegRead(s, 0) != 100) {
        std::fprintf(stderr, "[FAIL] TestFaultAbort: status=%d eip=%08x vec=%d addr=%08x eax=%u\n", X86_GetStatus(s),
                     X86_GetEIP(s), X86_GetFaultVector(s), X86_GetFaultAddress(s), X86_RegRead(s, 0));
        X86_Destroy(s);
        return false;
    }

    X86_Destroy(s);
    return true;
}

bool TestSegmentsAndTls() {
    X86_SetWasmJitEnabled(1);
    X86_SetWasmJitHotThreshold(1);

    EmuState* s = X86_Create();
    GuestMem mem;
    constexpr uint32_t kCode = 0x10000;
    constexpr uint32_t kTls = 0x70000;
    uint8_t* code = mem.MapPage(s, kCode, 5);
    uint8_t* tls = mem.MapPage(s, kTls, 3);
    uint32_t canary = 0xCAFEBABEu;
    std::memcpy(tls + 0x14, &canary, 4);

    // mov eax, gs:[0x14] (65 A1 14 00 00 00) ; add eax, 1 (83 C0 01) ; nop (90)
    const uint8_t prog[] = {0x65, 0xA1, 0x14, 0x00, 0x00, 0x00, 0x83, 0xC0, 0x01, 0x90};
    std::memcpy(code, prog, sizeof(prog));

    X86_SegBaseWrite(s, 5, kTls); // GS = seg_index 5 (seg_base[6])
    for (int i = 0; i < 3; ++i) {
        X86_SetEIP(s, kCode);
        X86_Run(s, kCode + sizeof(prog), 10000);
    }
    if (X86_RegRead(s, 0) != 0xCAFEBABFu) {
        std::fprintf(stderr, "[FAIL] TestTlsGs: eax=%08x (exp CAFEBABF)\n", X86_RegRead(s, 0));
        X86_Destroy(s);
        return false;
    }
    X86_Destroy(s);
    return true;
}

int RunSelfTests() {
    uint32_t regs0[8] = {0x12345678, 0x00000010, 0x80000005, 0x00020000, 0, 0x00020040, 0x00020000, 0x00020080};

    // 1. ALU + 64-bit ADC/SBB + shifts + rotates + IMUL + CDQ + CWDE + BSWAP
    const std::vector<uint8_t> alu_prog = {
        0x01, 0xD8,       // add eax, ebx
        0x11, 0xCA,       // adc edx, ecx
        0x29, 0xD0,       // sub eax, edx
        0x19, 0xC2,       // sbb edx, eax
        0x31, 0xC8,       // xor eax, ecx
        0x09, 0xDA,       // or  edx, ebx
        0x21, 0xD0,       // and eax, edx
        0x40,             // inc eax
        0x4A,             // dec edx
        0xC1, 0xE0, 0x05, // shl eax, 5
        0xC1, 0xEA, 0x03, // shr edx, 3
        0xC1, 0xF8, 0x02, // sar eax, 2
        0xC1, 0xC0, 0x07, // rol eax, 7
        0xC1, 0xCA, 0x09, // ror edx, 9
        0x0F, 0xAF, 0xC2, // imul eax, edx
        0x6B, 0xD0, 0xF3, // imul edx, eax, -13
        0x98,             // cwde
        0x99,             // cdq
        0x0F, 0xC8,       // bswap eax
        0xF7, 0xD0,       // not eax
        0xF7, 0xDA,       // neg edx
        0x90,
    };

    // 2. Tight self-loop (sum array at [ebx], count in ecx) + CMP/Jcc fusion + MOVZX/MOVSX/LEA
    const std::vector<uint8_t> loop_prog = {
        0x31, 0xC0,             // xor eax, eax
        0x8B, 0x16,             // .loop: mov edx, [esi] (offset 2)
        0x01, 0xD0,             // add eax, edx
        0x8D, 0x76, 0x04,       // lea esi, [esi + 4]
        0x49,                   // dec ecx
        0x75, 0xF6,             // jnz .loop (next=12, target=2 -> -10=0xF6)
        0x0F, 0xB6, 0x53, 0x01, // movzx edx, byte [ebx+1]
        0x0F, 0xBE, 0x4B, 0x02, // movsx ecx, byte [ebx+2]
        0x01, 0xD0,             // add eax, edx
        0x01, 0xC8,             // add eax, ecx
        0x90,
    };

    // 3. All condition codes via SETcc + CMOVcc + mid-block Jcc + cold DIV fallback!
    const std::vector<uint8_t> cond_cold_prog = {
        0x39,
        0xD0, // cmp eax, edx
        0x0F,
        0x9C,
        0xC1, // setl cl
        0x0F,
        0x9F,
        0xC5, // setg ch
        0x0F,
        0x42,
        0xC2, // cmovb eax, edx
        0x0F,
        0x4D,
        0x53,
        0x08, // cmovge edx, [ebx+8]
        0x85,
        0xC0, // test eax, eax
        0x74,
        0x04, // jz +4 (mid-block Jcc!)
        0x83,
        0xC0,
        0x07, // add eax, 7
        0x90, // nop
        // Cold DIV instruction in the middle of a block (triggers prefix + fallback suffix!)
        0x31,
        0xD2, // xor edx, edx
        0xB9,
        0x07,
        0x00,
        0x00,
        0x00, // mov ecx, 7
        0xF7,
        0xF1, // div ecx (COLD!)
        0x01,
        0xD0, // add eax, edx
        0x90,
    };

    // 4. PUSH / POP / CALL / RET / RET imm16 / LEAVE / XCHG mem
    const std::vector<uint8_t> call_prog = {
        0x50, // push eax
        0x6A,
        0x2A, // push 42
        0xE8,
        0x05,
        0x00,
        0x00,
        0x00, // call +5 (next=8 -> target=13)
        0x5B, // pop ebx
        0x87,
        0x07, // xchg [edi], eax
        0xEB,
        0x0D, // jmp end (next=13 -> target=26)
        // callee at offset 13:
        0x55, // push ebp
        0x89,
        0xE5, // mov ebp, esp
        0x8B,
        0x45,
        0x08, // mov eax, [ebp+8]
        0x03,
        0x45,
        0x0C, // add eax, [ebp+12]
        0xC9, // leave
        0xC2,
        0x04,
        0x00, // ret 4 (offset 23..25, end=26)
        0x90, // offset 26
    };

    // 5. 8-bit & 16-bit partial register + variable CL shifts + 0x66 opsize
    const std::vector<uint8_t> narrow_prog = {
        0x66, 0x01, 0xD8, // add ax, bx
        0x00, 0xE0,       // add al, ah
        0x28, 0xCC,       // sub ah, cl
        0xB1, 0x03,       // mov cl, 3
        0xD2, 0xE0,       // shl al, cl
        0xD2, 0xEC,       // shr ah, cl
        0x66, 0xD3, 0xF8, // sar ax, cl
        0x66, 0xD3, 0xC0, // rol ax, cl
        0x66, 0xD3, 0xCA, // ror dx, cl
        0xB1, 0x00,       // mov cl, 0 (must preserve flags!)
        0xD3, 0xE0,       // shl eax, cl
        0x90,
    };

    // 6. SSE2 scalar & packed FP (movsd mem/reg upper-bit semantics, addpd, mulpd, minsd/maxsd NaN & -0.0,
    //    andpd, andnpd, unpcklpd, shufpd, movddup, cvtsi2sd, cvtsd2ss, cvtss2sd, paddd, pshufd, psrldq/pslldq)
    const auto init_sse_data = [](uint8_t* data) {
        auto put_f64 = [&](size_t off, double v) { std::memcpy(data + off, &v, 8); };
        auto put_u64 = [&](size_t off, uint64_t v) { std::memcpy(data + off, &v, 8); };
        put_f64(0x00, 1.5);
        put_f64(0x08, -2.25);
        put_f64(0x10, 3.75);
        put_f64(0x18, 4.0);
        put_f64(0x20, 0.0);
        put_f64(0x28, -0.0);
        put_u64(0x30, 0x7FF8000000000001ull); // QNaN
        put_f64(0x38, 9.0);
        put_u64(0x40, 0x7FFFFFFFFFFFFFFFull); // abs mask
        put_u64(0x48, 0x7FFFFFFFFFFFFFFFull);
        put_f64(0x50, 2147483647.9);
        put_f64(0x58, 2147483648.0);  // overflow (+)
        put_f64(0x60, -2147483648.9); // truncates to INT32_MIN
        put_f64(0x68, -2147483649.0); // overflow (-)
        put_f64(0x70, std::numeric_limits<double>::infinity());
        put_f64(0x78, -std::numeric_limits<double>::infinity());
        // Cross-page test payload at 0xFF8 (spans page 0x20000 -> 0x21000)
        for (int i = 0; i < 32; ++i)
            data[0xFF0 + i] = static_cast<uint8_t>(0xA0 + i);
        // GEMM-like arrays at 0x200 (A), 0x300 (B), 0x400 (C)
        for (int i = 0; i < 32; ++i) {
            put_f64(0x200 + i * 8, 1.25 * (i + 1));
            put_f64(0x300 + i * 8, -0.5 * (i + 2));
            put_f64(0x400 + i * 8, 0.125 * i);
        }
    };

    const std::vector<uint8_t> sse2_fp_prog = {
        0x66, 0x0F, 0x28, 0x06,                         // movapd xmm0, [esi]          (1.5, -2.25)
        0x66, 0x0F, 0x28, 0x4E, 0x10,                   // movapd xmm1, [esi+0x10]     (3.75, 4.0)
        0x66, 0x0F, 0x59, 0xC1,                         // mulpd xmm0, xmm1
        0x66, 0x0F, 0x58, 0xC1,                         // addpd xmm0, xmm1
        0x66, 0x0F, 0x5C, 0xC8,                         // subpd xmm1, xmm0
        0x66, 0x0F, 0x54, 0x4E, 0x40,                   // andpd xmm1, [esi+0x40]
        0xF2, 0x0F, 0x10, 0x56, 0x38,                   // movsd xmm2, [esi+0x38]      (9.0, 0.0 -> zero upper!)
        0xF2, 0x0F, 0x51, 0xD2,                         // sqrtsd xmm2, xmm2           (3.0, 0.0)
        0xF2, 0x0F, 0x10, 0xC2,                         // movsd xmm0, xmm2            (3.0, preserve upper of xmm0!)
        0xF2, 0x0F, 0x10, 0x5E, 0x20,                   // movsd xmm3, [esi+0x20]      (+0.0)
        0xF2, 0x0F, 0x5D, 0x5E, 0x28,                   // minsd xmm3, [esi+0x28]      (+0.0 vs -0.0 -> -0.0!)
        0xF2, 0x0F, 0x10, 0x66, 0x30,                   // movsd xmm4, [esi+0x30]      (NaN)
        0xF2, 0x0F, 0x5F, 0x66, 0x38,                   // maxsd xmm4, [esi+0x38]      (NaN vs 9.0 -> 9.0!)
        0x66, 0x0F, 0x14, 0xE2,                         // unpcklpd xmm4, xmm2         (9.0, 3.0)
        0x66, 0x0F, 0xC6, 0xE0, 0x03,                   // shufpd xmm4, xmm0, 3
        0xF2, 0x0F, 0x12, 0x6E, 0x08,                   // movddup xmm5, [esi+0x08]
        0x66, 0x0F, 0x55, 0xE8,                         // andnpd xmm5, xmm0           ((~xmm5) & xmm0)
        0x66, 0x0F, 0xEF, 0xF6,                         // pxor xmm6, xmm6
        0xF2, 0x0F, 0x2A, 0xF0,                         // cvtsi2sd xmm6, eax
        0xF2, 0x0F, 0x5A, 0xFE,                         // cvtsd2ss xmm7, xmm6
        0xF3, 0x0F, 0x5A, 0xFF,                         // cvtss2sd xmm7, xmm7
        0x66, 0x0F, 0xFE, 0xF7,                         // paddd xmm6, xmm7
        0x66, 0x0F, 0x70, 0xF6, 0x1B,                   // pshufd xmm6, xmm6, 0x1B
        0x66, 0x0F, 0x73, 0xDE, 0x04,                   // psrldq xmm6, 4
        0x66, 0x0F, 0x73, 0xFE, 0x02,                   // pslldq xmm6, 2
        0x66, 0x0F, 0x29, 0x86, 0x80, 0x00, 0x00, 0x00, // movapd [esi+0x80], xmm0
        0xF2, 0x0F, 0x11, 0x9E, 0x90, 0x00, 0x00, 0x00, // movsd [esi+0x90], xmm3
        0x90,
    };

    // 7. UCOMISD / COMISD flags (<, >, ==, +0/-0, NaN unordered) + JP/JE/CMOVA/SBB
    const std::vector<uint8_t> sse2_ucomisd_prog = {
        0x31, 0xC0,                   // xor eax, eax
        0xF2, 0x0F, 0x10, 0x06,       // movsd xmm0, [esi+0x00] (1.5)
        0xF2, 0x0F, 0x10, 0x4E, 0x10, // movsd xmm1, [esi+0x10] (3.75)
        0x66, 0x0F, 0x2E, 0xC1,       // ucomisd xmm0, xmm1 (< => CF=1)
        0x83, 0xD0, 0x00,             // adc eax, 0 (eax=1)
        0x66, 0x0F, 0x2F, 0xC8,       // comisd xmm1, xmm0 (> => CF=0,ZF=0)
        0xBA, 0x10, 0x00, 0x00, 0x00, // mov edx, 0x10
        0x0F, 0x47, 0xC2,             // cmova eax, edx (eax=0x10)
        0xF2, 0x0F, 0x10, 0x56, 0x20, // movsd xmm2, [esi+0x20] (+0.0)
        0x66, 0x0F, 0x2E, 0x56, 0x28, // ucomisd xmm2, [esi+0x28] (-0.0 => ==, ZF=1,PF=0,CF=0)
        0x75, 0x03,                   // jne +3
        0x83, 0xC0, 0x04,             // add eax, 4 (eax=0x14)
        0xF2, 0x0F, 0x10, 0x5E, 0x30, // movsd xmm3, [esi+0x30] (NaN)
        0x66, 0x0F, 0x2E, 0xD8,       // ucomisd xmm3, xmm0 (unordered => ZF=1,PF=1,CF=1)
        0x7B, 0x03,                   // jnp +3
        0x83, 0xC0, 0x40,             // add eax, 0x40
        0x66, 0x0F, 0x2E, 0xD8,       // ucomisd xmm3, xmm0 (leave exact unordered flags at end!)
        0x90,
    };

    // 8. CVTTSD2SI & CVTSD2SI overflow, NaN, inf, boundary values
    const std::vector<uint8_t> sse2_cvt_prog = {
        0xF2, 0x0F, 0x2C, 0x46, 0x50,       // cvttsd2si eax, [esi+0x50] (2147483647.9 -> 0x7FFFFFFF)
        0xF2, 0x0F, 0x2C, 0x5E, 0x58,       // cvttsd2si ebx, [esi+0x58] (2147483648.0 -> 0x80000000)
        0xF2, 0x0F, 0x2C, 0x4E, 0x60,       // cvttsd2si ecx, [esi+0x60] (-2147483648.9 -> 0x80000000)
        0xF2, 0x0F, 0x2C, 0x56, 0x68,       // cvttsd2si edx, [esi+0x68] (-2147483649.0 -> 0x80000000)
        0xF2, 0x0F, 0x2C, 0x7E, 0x30,       // cvttsd2si edi, [esi+0x30] (NaN -> 0x80000000)
        0xF2, 0x0F, 0x2D, 0x6E, 0x00,       // cvtsd2si  ebp, [esi+0x00] (1.5 -> 2, ties-to-even!)
        0x89, 0x86, 0xA0, 0x00, 0x00, 0x00, // mov [esi+0xA0], eax
        0xF2, 0x0F, 0x2C, 0x46, 0x70,       // cvttsd2si eax, [esi+0x70] (+inf -> 0x80000000)
        0x89, 0x86, 0xA4, 0x00, 0x00, 0x00, // mov [esi+0xA4], eax
        0xF2, 0x0F, 0x2C, 0x46, 0x78,       // cvttsd2si eax, [esi+0x78] (-inf -> 0x80000000)
        0x90,
    };

    // 9. Cross-page 8B & 16B SSE2 slow-path read & write (spans 0x20000 -> 0x21000)
    const std::vector<uint8_t> sse2_crosspage_prog = {
        0x81, 0xC6, 0xF8, 0x0F, 0x00, 0x00, // add esi, 0xFF8 (addr = 0x20FF8)
        0xF3, 0x0F, 0x6F, 0x06,             // movdqu xmm0, [esi]          (crosses 0x21000!)
        0xF2, 0x0F, 0x10, 0x4E, 0x04,       // movsd  xmm1, [esi+4]        (8B crosses 0x21000!)
        0x66, 0x0F, 0x16, 0x4E, 0x05,       // movhpd xmm1, [esi+5]        (8B crosses 0x21000!)
        0x66, 0x0F, 0xFE, 0xC1,             // paddd  xmm0, xmm1
        0xF3, 0x0F, 0x7F, 0x46, 0x01,       // movdqu [esi+1], xmm0        (16B store crosses 0x21000!)
        0x66, 0x0F, 0x13, 0x4E, 0x03,       // movlpd [esi+3], xmm1        (8B store crosses 0x21000!)
        0x90,
    };

    // 10. Tight XMM self-loop (GEMM-style: movq + movhpd + mulpd + addpd + movlpd + movhpd + jne)
    const std::vector<uint8_t> sse2_selfloop_prog = {
        0xF2,
        0x0F,
        0x10,
        0x2E, // movsd xmm5, [esi]           (1.5)
        0x66,
        0x0F,
        0x14,
        0xED, // unpcklpd xmm5, xmm5         (1.5, 1.5)
        0x8D,
        0x9E,
        0x00,
        0x02,
        0x00,
        0x00, // lea ebx, [esi+0x200]        (A)
        0x8D,
        0x96,
        0x00,
        0x03,
        0x00,
        0x00, // lea edx, [esi+0x300]        (B)
        0xB9,
        0x10,
        0x00,
        0x00,
        0x00, // mov ecx, 16
        0x31,
        0xFF, // xor edi, edi
        // .loop (offset 27):
        0xF3,
        0x0F,
        0x7E,
        0x03, // movq xmm0, [ebx]            (4)
        0x66,
        0x0F,
        0x16,
        0x43,
        0x08, // movhpd xmm0, [ebx+8]        (5)
        0x66,
        0x0F,
        0x59,
        0xC5, // mulpd xmm0, xmm5            (4)
        0xF3,
        0x0F,
        0x7E,
        0x22, // movq xmm4, [edx]            (4)
        0x66,
        0x0F,
        0x16,
        0x62,
        0x08, // movhpd xmm4, [edx+8]        (5)
        0x66,
        0x0F,
        0x58,
        0xC4, // addpd xmm0, xmm4            (4)
        0x66,
        0x0F,
        0x13,
        0x02, // movlpd [edx], xmm0          (4)
        0x66,
        0x0F,
        0x17,
        0x42,
        0x08, // movhpd [edx+8], xmm0        (5)
        0x83,
        0xC7,
        0x01, // add edi, 1                  (3)
        0x83,
        0xC2,
        0x10, // add edx, 16                 (3)
        0x83,
        0xC3,
        0x10, // add ebx, 16                 (3)
        0x39,
        0xCF, // cmp edi, ecx                (2)
        0x75,
        0xD0, // jne .loop (-48 = 0xD0)      (2)
        0x90,
    };

    // 11. 16B alignment #GP(13) & 16B SSE2 SMC test
    auto test_sse2_align_and_smc = []() -> bool {
        X86_SetWasmJitEnabled(1);
        X86_SetWasmJitHotThreshold(1);
        EmuState* s = X86_Create();
        GuestMem mem;
        constexpr uint32_t kCode = 0x10000;
        constexpr uint32_t kData = 0x20000;
        constexpr uint32_t kStack = 0x30000;
        uint8_t* code_pg = mem.MapPage(s, kCode, 7); // RWX
        mem.MapPage(s, kData, 3);
        mem.MapPage(s, kStack, 3);

        // Unaligned movdqa xmm0, [ebx] -> must fault #GP(13)!
        const uint8_t align_prog[] = {0x66, 0x0F, 0x6F, 0x03, 0x90};
        std::memcpy(code_pg, align_prog, sizeof(align_prog));
        // Warm up aligned so it compiles to Wasm JIT
        for (int i = 0; i < 3; ++i) {
            X86_RegWrite(s, 3, kData);
            X86_SetEIP(s, kCode);
            X86_Run(s, kCode + sizeof(align_prog), 1000);
        }
        // Now unaligned!
        X86_RegWrite(s, 3, kData + 4);
        X86_SetEIP(s, kCode);
        X86_Run(s, kCode + sizeof(align_prog), 1000);
        if (X86_GetStatus(s) != 2 || X86_GetFaultVector(s) != 13 || X86_GetEIP(s) != kCode) {
            std::fprintf(stderr, "[FAIL] SSE2_AlignFault: st=%d vec=%d eip=%08x\n", X86_GetStatus(s),
                         X86_GetFaultVector(s), X86_GetEIP(s));
            X86_Destroy(s);
            return false;
        }
        X86_Destroy(s);

        // 16B movdqu SMC write into executable code page!
        EmuState* s2 = X86_Create();
        GuestMem mem2;
        uint8_t* pg = mem2.MapPage(s2, kCode, 7);
        uint8_t* dp = mem2.MapPage(s2, kData, 3);
        mem2.MapPage(s2, kStack, 3);
        // Target function at 0x10000: mov eax, 0x11111111; ret (padded to 16B)
        const uint8_t fn1[16] = {0xB8, 0x11, 0x11, 0x11, 0x11, 0xC3, 0x90, 0x90,
                                 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90};
        const uint8_t fn2[16] = {0xB8, 0x77, 0x66, 0x55, 0x44, 0xC3, 0x90, 0x90,
                                 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90};
        std::memcpy(pg, fn1, 16);
        std::memcpy(dp, fn2, 16);
        // Driver at 0x10020: call 0x10000; mov ebx, eax; call 0x10000; movdqu xmm0, [esi]; movdqu [edi], xmm0; call
        // 0x10000; nop
        const uint8_t drv[] = {
            0xE8, 0xDB, 0xFF, 0xFF, 0xFF, // call 0x10000
            0x89, 0xC3,                   // mov ebx, eax
            0xE8, 0xD4, 0xFF, 0xFF, 0xFF, // call 0x10000 (JIT compiled!)
            0xF3, 0x0F, 0x6F, 0x06,       // movdqu xmm0, [esi] (fn2)
            0xF3, 0x0F, 0x7F, 0x07,       // movdqu [edi], xmm0 (SMC overwrite of 0x10000!)
            0xE8, 0xC7, 0xFF, 0xFF, 0xFF, // call 0x10000 (must return 0x44556677!)
            0x90,
        };
        std::memcpy(pg + 0x20, drv, sizeof(drv));
        X86_RegWrite(s2, 6, kData);
        X86_RegWrite(s2, 7, kCode);
        X86_RegWrite(s2, 4, kStack + 0x800);
        X86_SetEIP(s2, kCode + 0x20);
        X86_Run(s2, kCode + 0x20 + sizeof(drv), 100000);
        const uint32_t ebx = X86_RegRead(s2, 3);
        const uint32_t eax = X86_RegRead(s2, 0);
        X86_Destroy(s2);
        if (ebx != 0x11111111u || eax != 0x44556677u) {
            std::fprintf(stderr, "[FAIL] SSE2_SMC_16B: ebx=%08x eax=%08x\n", ebx, eax);
            return false;
        }
        return true;
    };

    int passed = 0;
    auto check = [&](const char* name, bool ok) {
        if (ok) {
            std::printf("[PASS] %s\n", name);
            passed++;
        } else {
            std::fprintf(stderr, "[FAIL] %s\n", name);
        }
        return ok;
    };

    bool all_ok = true;
    all_ok &= check("ALU_ADC_SBB_Shift_Mul_Bswap", CompareSnippet("ALU", alu_prog, regs0));
    all_ok &= check("SelfLoop_LEA_MOVZX_MOVSX", CompareSnippet("Loop", loop_prog, regs0));
    all_ok &= check("Cond_SETcc_CMOVcc_MidJcc_ColdDIV", CompareSnippet("CondCold", cond_cold_prog, regs0));
    all_ok &= check("Stack_CALL_RET_Imm16_LEAVE_XCHG", CompareSnippet("CallStack", call_prog, regs0));
    all_ok &= check("Narrow_8_16_CL_Shift_Rotate", CompareSnippet("Narrow", narrow_prog, regs0));
    all_ok &=
        check("SSE2_Scalar_And_Packed_FP", CompareSnippet("SSE2_FP", sse2_fp_prog, regs0, 0x202, 2, init_sse_data));
    all_ok &= check("SSE2_Ucomisd_Comisd_NaN",
                    CompareSnippet("SSE2_UCOMISD", sse2_ucomisd_prog, regs0, 0x202, 2, init_sse_data));
    all_ok &=
        check("SSE2_Cvttsd2si_Overflow_NaN", CompareSnippet("SSE2_CVT", sse2_cvt_prog, regs0, 0x202, 2, init_sse_data));
    all_ok &= check("SSE2_CrossPage_16B_ReadWrite",
                    CompareSnippet("SSE2_CrossPage", sse2_crosspage_prog, regs0, 0x202, 2, init_sse_data));
    all_ok &= check("SSE2_SelfLoop_XMM_Accumulator",
                    CompareSnippet("SSE2_SelfLoop", sse2_selfloop_prog, regs0, 0x202, 2, init_sse_data));
    all_ok &= check("SSE2_AlignFault_And_16B_SMC", test_sse2_align_and_smc());
    all_ok &= check("SMC_And_CodeCacheRangeInvalidate", TestSmcAndInvalidation());
    all_ok &= check("WasmTablePool_Exhaustion_Eviction", TestPoolExhaustion());
    all_ok &= check("DemandFault_Replay_And_Abort", TestDemandFaultAndAbort());
    all_ok &= check("Segment_GS_TLS", TestSegmentsAndTls());

    char stats[1024];
    X86_DumpWasmJitStats(stats, sizeof(stats));
    if (std::strstr(stats, "\"compile_failures\":0") == nullptr && std::strstr(stats, "\"enabled\":1") != nullptr) {
        std::fprintf(stderr, "[FAIL] Non-zero compile_failures in selftest!\n");
        all_ok = false;
    }
    std::printf("SelfTest summary: %d/15 passed. WasmJIT stats: %s\n", passed, stats);
    return all_ok ? 0 : 1;
}

// ============================================================================
// Minimal Static i386 Linux ELF Loader & Syscall Shim
// ============================================================================

struct Elf32Ehdr {
    uint8_t e_ident[16];
    uint16_t e_type, e_machine;
    uint32_t e_version, e_entry, e_phoff, e_shoff, e_flags;
    uint16_t e_ehsize, e_phentsize, e_phnum, e_shentsize, e_shnum, e_shstrndx;
};

struct Elf32Phdr {
    uint32_t p_type, p_offset, p_vaddr, p_paddr, p_filesz, p_memsz, p_flags, p_align;
};

struct LinuxEnv {
    GuestMem mem;
    uint32_t brk_base = 0;
    uint32_t brk_curr = 0;
    uint32_t mmap_next = 0x40000000u;
    int exit_code = 0;
    bool exited = false;
    bool hash_stderr = false;
    bool suppress_stderr = false;
    Sha256 stderr_sha;
    std::unordered_map<int, int> fd_map;
    int next_fd = 3;
    std::vector<int64_t> cpu_clock_trace_us;
    size_t cpu_clock_index = 0;
};

int64_t GuestClockNanoseconds(LinuxEnv& env, EmuState* state, uint32_t guest_clock_id) {
    constexpr uint32_t kLinuxProcessCpuClock = 2;
    if (guest_clock_id == kLinuxProcessCpuClock && !env.cpu_clock_trace_us.empty()) {
        if (env.cpu_clock_index == env.cpu_clock_trace_us.size()) {
            std::fprintf(stderr, "Guest CPU clock replay exhausted\n");
            env.exit_code = 2;
            env.exited = true;
            X86_EmuStop(state);
            return 0;
        }
        return env.cpu_clock_trace_us[env.cpu_clock_index++] * 1000;
    }
    auto now = std::chrono::steady_clock::now().time_since_epoch();
    return std::chrono::duration_cast<std::chrono::nanoseconds>(now).count();
}

int32_t TranslateHostErrno(int err) {
    return err > 0 ? -err : -1;
}

int LinuxSyscallHook(EmuState* state, uint32_t vector, void* userdata) {
    if (vector != 0x80)
        return 0;
    auto* env = static_cast<LinuxEnv*>(userdata);
    const uint32_t nr = X86_RegRead(state, 0); // EAX
    const uint32_t a1 = X86_RegRead(state, 3); // EBX
    const uint32_t a2 = X86_RegRead(state, 1); // ECX
    const uint32_t a3 = X86_RegRead(state, 2); // EDX
    const uint32_t a4 = X86_RegRead(state, 6); // ESI
    const uint32_t a5 = X86_RegRead(state, 7); // EDI
    const uint32_t a6 = X86_RegRead(state, 5); // EBP
    (void)a4;
    (void)a5;
    (void)a6;

    int32_t ret = -38; // -ENOSYS

    auto emit_fd_write = [&](int fd, const uint8_t* data, size_t len) -> int32_t {
        if (fd == 2) {
            if (env->hash_stderr)
                env->stderr_sha.Update(data, len);
            if (env->suppress_stderr)
                return static_cast<int32_t>(len);
            ssize_t r = ::write(2, data, len);
            return r < 0 ? TranslateHostErrno(errno) : static_cast<int32_t>(r);
        }
        int host_fd = (fd == 1) ? 1 : (env->fd_map.count(fd) ? env->fd_map[fd] : -1);
        if (host_fd < 0)
            return -9; // EBADF
        ssize_t r = ::write(host_fd, data, len);
        return r < 0 ? TranslateHostErrno(errno) : static_cast<int32_t>(r);
    };

    switch (nr) {
    case 1:   // exit
    case 252: // exit_group
        env->exit_code = static_cast<int>(a1);
        env->exited = true;
        X86_EmuStop(state);
        return 1;

    case 3: { // read(fd, buf, count)
        int fd = static_cast<int>(a1);
        int host_fd = (fd == 0) ? 0 : (env->fd_map.count(fd) ? env->fd_map[fd] : -1);
        if (host_fd < 0) {
            ret = -9;
            break;
        }
        std::vector<uint8_t> tmp(std::min<uint32_t>(a3, 1u << 20));
        ssize_t r = ::read(host_fd, tmp.data(), tmp.size());
        if (r < 0) {
            ret = TranslateHostErrno(errno);
        } else {
            if (r > 0)
                env->mem.WriteBytes(state, a2, tmp.data(), static_cast<size_t>(r));
            ret = static_cast<int32_t>(r);
        }
        break;
    }

    case 4: { // write(fd, buf, count)
        std::vector<uint8_t> tmp(a3);
        if (a3 > 0 && !env->mem.ReadBytes(state, a2, tmp.data(), a3)) {
            ret = -14; // EFAULT
            break;
        }
        ret = emit_fd_write(static_cast<int>(a1), tmp.data(), a3);
        break;
    }

    case 146: { // writev(fd, iov, iovcnt)
        int fd = static_cast<int>(a1);
        int32_t total = 0;
        for (uint32_t i = 0; i < a3; ++i) {
            uint32_t pair[2] = {};
            if (!env->mem.ReadBytes(state, a2 + i * 8, pair, 8)) {
                total = -14;
                break;
            }
            if (pair[1] == 0)
                continue;
            std::vector<uint8_t> tmp(pair[1]);
            if (!env->mem.ReadBytes(state, pair[0], tmp.data(), pair[1])) {
                total = -14;
                break;
            }
            int32_t w = emit_fd_write(fd, tmp.data(), pair[1]);
            if (w < 0) {
                total = w;
                break;
            }
            total += w;
        }
        ret = total;
        break;
    }

    case 5: { // open(path, flags, mode)
        std::string path = env->mem.ReadCString(state, a1);
        int hfd = ::open(path.c_str(), O_RDONLY);
        if (hfd < 0) {
            ret = TranslateHostErrno(errno);
        } else {
            int gfd = env->next_fd++;
            env->fd_map[gfd] = hfd;
            ret = gfd;
        }
        break;
    }

    case 295: { // openat(dirfd, path, flags, mode)
        std::string path = env->mem.ReadCString(state, a2);
        int hfd = ::open(path.c_str(), O_RDONLY);
        if (hfd < 0) {
            ret = TranslateHostErrno(errno);
        } else {
            int gfd = env->next_fd++;
            env->fd_map[gfd] = hfd;
            ret = gfd;
        }
        break;
    }

    case 6: { // close(fd)
        int fd = static_cast<int>(a1);
        auto it = env->fd_map.find(fd);
        if (it != env->fd_map.end()) {
            ::close(it->second);
            env->fd_map.erase(it);
            ret = 0;
        } else {
            ret = (fd >= 0 && fd <= 2) ? 0 : -9;
        }
        break;
    }

    case 45: { // brk(addr)
        if (a1 == 0 || a1 < env->brk_base) {
            ret = static_cast<int32_t>(env->brk_curr);
        } else {
            if (a1 > env->brk_curr) {
                uint32_t old_end = (env->brk_curr + 0xFFFu) & ~0xFFFu;
                uint32_t new_end = (a1 + 0xFFFu) & ~0xFFFu;
                if (new_end > old_end)
                    env->mem.MapRange(state, old_end, new_end - old_end, 3);
            }
            env->brk_curr = a1;
            ret = static_cast<int32_t>(env->brk_curr);
        }
        break;
    }

    case 90:    // old_mmap
    case 192: { // mmap2(addr, len, prot, flags, fd, pgoffset)
        uint32_t req_addr = a1, len = a2, prot = a3;
        if (nr == 90) {
            uint32_t args[6]{};
            env->mem.ReadBytes(state, a1, args, sizeof(args));
            req_addr = args[0];
            len = args[1];
            prot = args[2];
        }
        uint32_t aligned_len = (len + 0xFFFu) & ~0xFFFu;
        if (aligned_len == 0)
            aligned_len = 4096;
        uint32_t addr = req_addr ? (req_addr & ~0xFFFu) : env->mmap_next;
        if (!req_addr)
            env->mmap_next += aligned_len;
        uint8_t perms = static_cast<uint8_t>(prot & 7);
        if (perms == 0)
            perms = 3;
        env->mem.MapRange(state, addr, aligned_len, perms);
        ret = static_cast<int32_t>(addr);
        break;
    }

    case 91:  // munmap
    case 125: // mprotect
        ret = 0;
        break;

    case 54:       // ioctl
        ret = -25; // -ENOTTY
        break;

    case 122: { // uname
        struct LinuxUts {
            char sysname[65], nodename[65], release[65], version[65], machine[65], domain[65];
        } uts{};
        std::strcpy(uts.sysname, "Linux");
        std::strcpy(uts.nodename, "fiber");
        std::strcpy(uts.release, "6.1.0");
        std::strcpy(uts.version, "#1");
        std::strcpy(uts.machine, "i686");
        env->mem.WriteBytes(state, a1, &uts, sizeof(uts));
        ret = 0;
        break;
    }

    case 243: { // set_thread_area(struct user_desc *u_info)
        uint32_t u_info[4]{};
        env->mem.ReadBytes(state, a1, u_info, sizeof(u_info));
        int32_t entry_num = static_cast<int32_t>(u_info[0]);
        uint32_t base_addr = u_info[1];
        if (entry_num == -1) {
            entry_num = 6;
            u_info[0] = static_cast<uint32_t>(entry_num);
            env->mem.WriteBytes(state, a1, &u_info[0], 4);
        }
        X86_SegBaseWrite(state, 5, base_addr); // GS
        ret = 0;
        break;
    }

    case 258: // set_tid_address
    case 20:  // getpid
    case 224: // gettid
        ret = 1000;
        break;

    case 24:  // getuid
    case 47:  // getgid
    case 49:  // geteuid
    case 50:  // getegid
    case 199: // getuid32
    case 200: // getgid32
    case 201: // geteuid32
    case 202: // getegid32
        ret = 0;
        break;

    case 78: { // gettimeofday(tv, tz)
        auto now = std::chrono::system_clock::now().time_since_epoch();
        auto usec = std::chrono::duration_cast<std::chrono::microseconds>(now).count();
        uint32_t tv[2] = {static_cast<uint32_t>(usec / 1000000), static_cast<uint32_t>(usec % 1000000)};
        if (a1)
            env->mem.WriteBytes(state, a1, tv, sizeof(tv));
        ret = 0;
        break;
    }

    case 265: { // clock_gettime (32-bit timespec)
        const int64_t nsec = GuestClockNanoseconds(*env, state, a1);
        uint32_t ts[2] = {static_cast<uint32_t>(nsec / 1000000000LL), static_cast<uint32_t>(nsec % 1000000000LL)};
        if (a2)
            env->mem.WriteBytes(state, a2, ts, sizeof(ts));
        ret = 0;
        break;
    }

    case 403: { // clock_gettime64 (64-bit timespec!)
        const int64_t nsec = GuestClockNanoseconds(*env, state, a1);
        int64_t ts[2] = {nsec / 1000000000LL, nsec % 1000000000LL};
        if (a2)
            env->mem.WriteBytes(state, a2, ts, sizeof(ts));
        ret = 0;
        break;
    }

    case 266: { // clock_getres
        uint32_t ts[2] = {0, 1};
        if (a2)
            env->mem.WriteBytes(state, a2, ts, sizeof(ts));
        ret = 0;
        break;
    }

    case 406: { // clock_getres_time64
        int64_t ts[2] = {0, 1};
        if (a2)
            env->mem.WriteBytes(state, a2, ts, sizeof(ts));
        ret = 0;
        break;
    }

    case 174: // rt_sigaction
    case 175: // rt_sigprocmask
    case 240: // futex
    case 191: // ugetrlimit
    case 340: // prlimit64
        ret = 0;
        break;

    case 355: { // getrandom(buf, buflen, flags)
        std::vector<uint8_t> rnd(a2);
        for (uint32_t i = 0; i < a2; ++i)
            rnd[i] = static_cast<uint8_t>((i * 131 + 17) & 0xFF);
        env->mem.WriteBytes(state, a1, rnd.data(), a2);
        ret = static_cast<int32_t>(a2);
        break;
    }

    case 197: { // fstat64
        uint8_t st[96]{};
        uint32_t mode = (a1 <= 2) ? 0020620u : 0100644u;
        std::memcpy(st + 16, &mode, 4);
        env->mem.WriteBytes(state, a2, st, sizeof(st));
        ret = 0;
        break;
    }

    case 85:      // readlink
    case 305:     // readlinkat
        ret = -2; // ENOENT
        break;

    case 300:      // fstatat64
    case 311:      // set_robust_list
    case 383:      // statx
    case 386:      // rseq
        ret = -38; // -ENOSYS (optional glibc startup probes)
        break;

    default:
        std::fprintf(stderr, "[LinuxShim] Unhandled syscall nr=%u (a1=%x a2=%x a3=%x)\n", nr, a1, a2, a3);
        ret = -38;
        break;
    }

    X86_RegWrite(state, 0, static_cast<uint32_t>(ret));
    return 1;
}

int RunStaticElf(const std::string& elf_path, const std::vector<std::string>& guest_args, bool hash_stderr,
                 bool suppress_stderr, bool dump_stats, const std::string& cpu_clock_trace) {
    std::ifstream f(elf_path, std::ios::binary | std::ios::ate);
    if (!f) {
        std::fprintf(stderr, "Failed to open ELF: %s\n", elf_path.c_str());
        return 2;
    }
    const size_t file_size = static_cast<size_t>(f.tellg());
    f.seekg(0);
    std::vector<uint8_t> elf(file_size);
    if (!f.read(reinterpret_cast<char*>(elf.data()), static_cast<std::streamsize>(file_size))) {
        std::fprintf(stderr, "Failed to read ELF: %s\n", elf_path.c_str());
        return 2;
    }

    Elf32Ehdr ehdr{};
    if (file_size < sizeof(ehdr))
        return 2;
    std::memcpy(&ehdr, elf.data(), sizeof(ehdr));
    if (std::memcmp(ehdr.e_ident,
                    "\x7f"
                    "ELF",
                    4) != 0 ||
        ehdr.e_ident[4] != 1 || ehdr.e_machine != 3) {
        std::fprintf(stderr, "Not a 32-bit i386 ELF: %s\n", elf_path.c_str());
        return 2;
    }

    LinuxEnv env;
    env.hash_stderr = hash_stderr;
    env.suppress_stderr = suppress_stderr;
    if (!cpu_clock_trace.empty()) {
        std::ifstream trace(cpu_clock_trace);
        int64_t usec = 0;
        while (trace >> usec) {
            if (usec < 0 || usec > std::numeric_limits<int64_t>::max() / 1000 ||
                (!env.cpu_clock_trace_us.empty() && usec < env.cpu_clock_trace_us.back())) {
                std::fprintf(stderr, "Invalid guest CPU clock timestamp in %s\n", cpu_clock_trace.c_str());
                return 2;
            }
            env.cpu_clock_trace_us.push_back(usec);
        }
        if (!trace.eof() || env.cpu_clock_trace_us.empty()) {
            std::fprintf(stderr, "Unable to read guest CPU clock trace: %s\n", cpu_clock_trace.c_str());
            return 2;
        }
    }
    EmuState* state = X86_Create();

    uint32_t max_end = 0;
    uint32_t phdr_vaddr = 0;
    for (uint16_t i = 0; i < ehdr.e_phnum; ++i) {
        Elf32Phdr ph{};
        std::memcpy(&ph, elf.data() + ehdr.e_phoff + i * ehdr.e_phentsize, sizeof(ph));
        if (ph.p_type == 6) { // PT_PHDR
            phdr_vaddr = ph.p_vaddr;
        }
        if (ph.p_type != 1)
            continue; // PT_LOAD
        if (phdr_vaddr == 0 && ph.p_offset <= ehdr.e_phoff && ehdr.e_phoff < ph.p_offset + ph.p_filesz) {
            phdr_vaddr = ph.p_vaddr + (ehdr.e_phoff - ph.p_offset);
        }
        uint8_t perms = 0;
        if (ph.p_flags & 4)
            perms |= 1; // R
        if (ph.p_flags & 2)
            perms |= 2; // W
        if (ph.p_flags & 1)
            perms |= 4; // X
        // Note: map RW first if writing, then protect, OR write directly to backing host pages!
        env.mem.MapRange(state, ph.p_vaddr, ph.p_memsz, perms);
        if (ph.p_filesz > 0) {
            env.mem.WriteBytes(state, ph.p_vaddr, elf.data() + ph.p_offset, ph.p_filesz);
        }
        max_end = std::max(max_end, ph.p_vaddr + ph.p_memsz);
    }

    env.brk_base = (max_end + 0xFFFu) & ~0xFFFu;
    env.brk_curr = env.brk_base;

    // Map 8 MiB stack at [0xBF800000, 0xC0000000)
    constexpr uint32_t kStackTop = 0xBFFF0000u;
    constexpr uint32_t kStackSize = 8u * 1024u * 1024u;
    env.mem.MapRange(state, kStackTop - kStackSize, kStackSize, 3);

    // Build initial Linux i386 stack: random bytes, argv strings, envp, auxv, argc
    uint32_t sp = kStackTop - 256;
    uint8_t random_bytes[16] = {1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16};
    sp -= 16;
    const uint32_t at_random_addr = sp;
    env.mem.WriteBytes(state, at_random_addr, random_bytes, 16);

    std::vector<uint32_t> argv_addrs;
    for (const auto& arg : guest_args) {
        sp -= static_cast<uint32_t>(arg.size() + 1);
        env.mem.WriteBytes(state, sp, arg.c_str(), arg.size() + 1);
        argv_addrs.push_back(sp);
    }
    sp &= ~15u;

    std::vector<uint32_t> words;
    words.push_back(static_cast<uint32_t>(argv_addrs.size())); // argc
    for (uint32_t a : argv_addrs)
        words.push_back(a);
    words.push_back(0); // argv NULL
    words.push_back(0); // envp NULL

    // auxv
    auto push_aux = [&](uint32_t k, uint32_t v) {
        words.push_back(k);
        words.push_back(v);
    };
    push_aux(3, phdr_vaddr);       // AT_PHDR
    push_aux(4, ehdr.e_phentsize); // AT_PHENT
    push_aux(5, ehdr.e_phnum);     // AT_PHNUM
    push_aux(6, 4096);             // AT_PAGESZ
    push_aux(9, ehdr.e_entry);     // AT_ENTRY
    push_aux(17, 100);             // AT_CLKTCK
    push_aux(25, at_random_addr);  // AT_RANDOM
    push_aux(0, 0);                // AT_NULL

    sp -= static_cast<uint32_t>(words.size() * 4);
    sp &= ~15u;
    env.mem.WriteBytes(state, sp, words.data(), words.size() * 4);

    X86_RegWrite(state, 4, sp); // ESP
    X86_SetEIP(state, ehdr.e_entry);
    X86_SetInterruptHook(state, 0x80, LinuxSyscallHook, &env);

    const auto t0 = std::chrono::steady_clock::now();
    while (!env.exited) {
        X86_Run(state, 0, 0);
        const int st = X86_GetStatus(state);
        if (env.exited)
            break;
        if (st == 4) { // CachePressure
            X86_ResetAllCodeCache(state);
            continue;
        }
        if (st == 2) { // Fault
            std::fprintf(stderr, "[ELF] Guest FAULT at EIP=0x%08x vec=%d addr=0x%08x\n", X86_GetEIP(state),
                         X86_GetFaultVector(state), X86_GetFaultAddress(state));
            X86_Destroy(state);
            return 3;
        }
        if (st == 1)
            break;
    }
    const auto t1 = std::chrono::steady_clock::now();
    const double wall_ms = std::chrono::duration<double, std::milli>(t1 - t0).count();

    if (hash_stderr) {
        std::printf("STDERR_SHA256: %s\n", env.stderr_sha.HexDigest().c_str());
    }
    if (dump_stats) {
        char buf[2048];
        X86_DumpWasmJitStats(buf, sizeof(buf));
        std::printf("WALL_MS: %.3f\n", wall_ms);
        std::printf("WASM_JIT_STATS: %s\n", buf);
    }

    X86_Destroy(state);
    if (env.cpu_clock_index != env.cpu_clock_trace_us.size()) {
        std::fprintf(stderr, "Guest CPU clock replay consumed %zu/%zu timestamps\n", env.cpu_clock_index,
                     env.cpu_clock_trace_us.size());
        return 2;
    }
    return env.exit_code;
}

} // namespace

int main(int argc, char** argv) {
    bool selftest = false;
    bool hash_stderr = false;
    bool suppress_stderr = false;
    bool dump_stats = false;
    std::string cpu_clock_trace;
    int elf_arg_start = -1;

    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "--selftest") {
            selftest = true;
        } else if (a == "--jit=on" || a == "--jit=1") {
            X86_SetWasmJitEnabled(1);
        } else if (a == "--jit=off" || a == "--jit=0") {
            X86_SetWasmJitEnabled(0);
        } else if (a == "--stats") {
            dump_stats = true;
        } else if (a == "--dyn-stats") {
            X86_SetWasmJitStatsEnabled(1);
            dump_stats = true;
        } else if (a.rfind("--hot=", 0) == 0) {
            X86_SetWasmJitHotThreshold(static_cast<uint16_t>(std::strtoul(a.c_str() + 6, nullptr, 10)));
        } else if (a.rfind("--pool=", 0) == 0) {
            X86_SetWasmJitPoolSize(static_cast<uint32_t>(std::strtoul(a.c_str() + 7, nullptr, 10)));
        } else if (a == "--stderr-sha256") {
            hash_stderr = true;
            suppress_stderr = true;
        } else if (a.rfind("--cpu-clock-trace=", 0) == 0) {
            cpu_clock_trace = a.substr(sizeof("--cpu-clock-trace=") - 1);
        } else if (a == "--") {
            elf_arg_start = i + 1;
            break;
        } else if (!a.empty() && a[0] != '-') {
            elf_arg_start = i;
            break;
        }
    }

    if (selftest)
        return RunSelfTests();
    if (elf_arg_start < 0 || elf_arg_start >= argc) {
        std::fprintf(stderr,
                     "Usage: %s [--selftest] [--jit=on|off] [--stats] [--dyn-stats] [--hot=N] [--pool=N] "
                     "[--stderr-sha256] [--cpu-clock-trace=FILE] [--] <elf> [guest args...]\n",
                     argv[0]);
        return 2;
    }

    std::vector<std::string> guest_args;
    for (int i = elf_arg_start; i < argc; ++i)
        guest_args.emplace_back(argv[i]);
    return RunStaticElf(guest_args[0], guest_args, hash_stderr, suppress_stderr, dump_stats, cpu_clock_trace);
}
