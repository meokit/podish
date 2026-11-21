#include "jit/llvm_block_cache.h"
#include "jit/llvm_operand_ir.h"

#include <array>

#include <llvm/Bitcode/BitcodeReader.h>
#include <llvm/ExecutionEngine/Orc/CompileUtils.h>
#include <llvm/IR/Module.h>
#include <llvm/Support/MemoryBuffer.h>
#include <llvm/Support/raw_ostream.h>

namespace fiberish {
namespace {

constexpr block_recipe::Operation kOperations[] = {
#define FIBERCPU_LLVM_OP(Id, Name, Exact, Requirements, Extensions) {block_recipe::OpId{Id}, Requirements, Extensions},
#include "llvm_block_operations.inc"
#undef FIBERCPU_LLVM_OP
};

static_assert(std::size(kOperations) == block_recipe::kOpIdCount);

bool ReadStep(llvm::StringRef& line, block_recipe::BoundOperation& operation, DecodedOperandShape& shape) {
    const auto [token, operands] = line.split(' ');
    uint16_t raw_id;
    if (token.getAsInteger(10, raw_id) || raw_id >= std::size(kOperations)) return false;
    operation.id = block_recipe::OpId{raw_id};
    line = operands;
    for (auto* field : {&shape.encoding, &shape.ea_desc, &shape.displacement, &shape.immediate}) {
        const auto [operand, next] = line.split(' ');
        if (operand.getAsInteger(10, *field)) return false;
        line = next;
    }
    operation.shape = shape.Binding();
    return true;
}

bool SupportsShape(block_recipe::OpId id, const DecodedOperandShape& shape) {
    DecodedInstruction op{};
    op.modrm = uint8_t(shape.encoding);
    op.prefixes.all = uint8_t(shape.encoding >> 8);
    op.meta.all = uint8_t(shape.encoding >> 16);
    op.len = uint8_t(shape.encoding >> 24);
    return block_recipe::CanPlanOp(op) && kOperations[static_cast<size_t>(id)].Matches(op, true);
}

llvm::Error Run(const char* bitcode, const char* requests, const char* output) {
    if (!InitializeLLVMTarget()) return llvm::createStringError("failed to initialize LLVM native target");
    auto input = llvm::MemoryBuffer::getFile(bitcode);
    if (!input) return llvm::errorCodeToError(input.getError());
    llvm::LLVMContext context;
    auto module = llvm::parseBitcodeFile((*input)->getMemBufferRef(), context);
    if (!module) return module.takeError();
    LLVMOperandTemplates templates(module->get());
    if (!templates.IsValid()) return llvm::createStringError("invalid LLVM operand template table");

    auto machine = CreateLLVMObjectTarget(**module);
    if (!machine) return machine.takeError();
    llvm::orc::TMOwningSimpleCompiler compiler(std::move(*machine));

    auto plan = llvm::MemoryBuffer::getFile(requests);
    if (!plan) return llvm::errorCodeToError(plan.getError());
    std::error_code error;
    llvm::raw_fd_ostream out(output, error);
    if (error) return llvm::errorCodeToError(error);
    auto [fingerprint, rows] = (*plan)->getBuffer().split('\n');
    if (fingerprint != llvm::StringRef(block_recipe::kOpIdFingerprint))
        return llvm::createStringError("LLVM operand request ID table differs from this build");
    while (!rows.empty()) {
        auto [line, rest] = rows.split('\n');
        rows = rest;
        std::array<block_recipe::BoundOperation, 4> operations;
        std::array<DecodedOperandShape, 4> operands;
        std::size_t count = 0;
        bool supported = true;
        while (!line.empty()) {
            if (count == operations.size() || !ReadStep(line, operations[count], operands[count]))
                return llvm::createStringError("invalid LLVM operand stencil request");
            supported &= SupportsShape(operations[count].id, operands[count]);
            ++count;
        }
        if (!count) return llvm::createStringError("empty LLVM operand stencil request");
        if (!supported) {
            if (!llvm_block_cache::WriteRecord(out, "-")) return llvm::createStringError("cannot write rejection");
            continue;
        }
        const block_recipe::BlockKeyView key{LLVMBlockExit::Dispatch, std::span(operations).first(count),
                                             std::span(operands).first(count)};
        auto module = PrepareLLVMBlock(templates, key, "FiberOperandStencil");
        if (!module)
            return llvm::createStringError("cannot prepare LLVM operand stencil for ID %u",
                                           static_cast<unsigned>(operations.front().id));
        auto object = compiler(*module);
        if (!object) return object.takeError();
        // Keep the native packer inside the same binding when it groups
        // objects by instruction bytes for immediate patching.
        llvm::SmallVector<char, 256> record;
        for (const auto& operation : key.operations) {
            record.push_back(static_cast<char>(operation.shape.modrm));
            record.push_back(static_cast<char>(operation.shape.slots));
        }
        const auto buffer = (*object)->getBuffer();
        record.append(buffer.begin(), buffer.end());
        if (!llvm_block_cache::WriteRecord(out, std::string_view(record.data(), record.size())))
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
