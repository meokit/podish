#include "jit/llvm_block_cache.h"
#include "jit/llvm_operand_ir.h"

#include <algorithm>
#include <array>
#include <charconv>

#include <llvm/Bitcode/BitcodeReader.h>
#include <llvm/ExecutionEngine/Orc/CompileUtils.h>
#include <llvm/IR/Module.h>
#include <llvm/Support/MemoryBuffer.h>
#include <llvm/Support/raw_ostream.h>

namespace fiberish {
namespace {

constexpr block_recipe::Operation kOperations[] = {
#define FIBERCPU_LLVM_OP(Name, Exact, Requirements, Extensions) {#Name, Requirements, Extensions},
#include "llvm_block_operations.inc"
#undef FIBERCPU_LLVM_OP
};

bool ReadStep(llvm::StringRef& line, block_recipe::Step& step) {
    const auto [name, operands] = line.split(' ');
    step.name = name;
    line = operands;
    for (auto* field : {&step.shape.encoding, &step.shape.ea_desc, &step.shape.displacement, &step.shape.immediate}) {
        const auto [token, next] = line.split(' ');
        if (token.getAsInteger(10, *field)) return false;
        line = next;
    }
    return !name.empty();
}

bool SupportsShape(const block_recipe::Step& step) {
    const auto operation = std::ranges::lower_bound(kOperations, step.name, {}, &block_recipe::Operation::name);
    if (operation == std::end(kOperations) || step.name != operation->name) return false;
    DecodedOp op{};
    op.modrm = uint8_t(step.shape.encoding);
    op.prefixes.all = uint8_t(step.shape.encoding >> 8);
    op.meta.all = uint8_t(step.shape.encoding >> 16);
    op.len = uint8_t(step.shape.encoding >> 24);
    return block_recipe::CanPlanOp(op) && operation->Matches(op, true);
}

llvm::Error Run(const char* bitcode, const char* requests, const char* output) {
    if (!InitializeLLVMTarget()) return llvm::createStringError("failed to initialize LLVM native target");
    auto input = llvm::MemoryBuffer::getFile(bitcode);
    if (!input) return llvm::errorCodeToError(input.getError());
    llvm::LLVMContext context;
    auto templates = llvm::parseBitcodeFile((*input)->getMemBufferRef(), context);
    if (!templates) return templates.takeError();

    auto machine = CreateLLVMObjectTarget(**templates);
    if (!machine) return machine.takeError();
    llvm::orc::TMOwningSimpleCompiler compiler(std::move(*machine));

    auto plan = llvm::MemoryBuffer::getFile(requests);
    if (!plan) return llvm::errorCodeToError(plan.getError());
    std::error_code error;
    llvm::raw_fd_ostream out(output, error);
    if (error) return llvm::errorCodeToError(error);
    auto rows = (*plan)->getBuffer();
    while (!rows.empty()) {
        auto [line, rest] = rows.split('\n');
        rows = rest;
        std::array<block_recipe::Step, 4> steps;
        std::size_t count = 0;
        while (!line.empty()) {
            if (count == steps.size() || !ReadStep(line, steps[count++]))
                return llvm::createStringError("invalid LLVM operand stencil request");
        }
        const auto block = std::span(steps).first(count);
        if (block.empty()) return llvm::createStringError("empty LLVM operand stencil request");
        if (!std::ranges::all_of(block, SupportsShape)) {
            if (!llvm_block_cache::WriteRecord(out, "-")) return llvm::createStringError("cannot write rejection");
            continue;
        }
        auto module = PrepareLLVMBlock(**templates, block, true, LLVMBlockExit::Dispatch, "FiberOperandStencil");
        if (!module)
            return llvm::createStringError("cannot prepare LLVM operand stencil for %s",
                                           std::string(block.front().name).c_str());
        auto object = compiler(*module);
        if (!object) return object.takeError();
        if (!llvm_block_cache::WriteRecord(out, (*object)->getBuffer()))
            return llvm::createStringError("cannot write stencil object");
    }
    out.close();
    return out.has_error() ? llvm::errorCodeToError(out.error()) : llvm::Error::success();
}

}  // namespace
}  // namespace fiberish

int main(int argc, char** argv) {
    if (argc != 4) {
        llvm::errs() << "usage: fibercpu_operand_stencils templates.bc requests.txt objects.bin\n";
        return 1;
    }
    if (auto error = fiberish::Run(argv[1], argv[2], argv[3])) {
        llvm::logAllUnhandledErrors(std::move(error), llvm::errs(), "LLVM operand stencils: ");
        return 1;
    }
    return 0;
}
