#!/usr/bin/env python3
"""
自动化重构脚本：将 ops.cpp 拆分为模块化文件

使用的库：
- re: 正则表达式匹配函数签名
- pathlib: 跨平台路径处理
- 标准库处理括号匹配和文本提取
"""

import re
from pathlib import Path
from typing import Dict, List, Tuple, Optional

# 定义每个类别及其包含的函数
CATEGORIES = {
    'sse_fp': {
        'desc': 'SSE/SSE2 Floating Point Operations',
        'functions': [
            'OpAdd_Sse', 'OpSub_Sse', 'OpMul_Sse', 'OpDiv_Sse',
            'OpAnd_Sse', 'OpAndn_Sse',
            'OpCmp_Sse', 'OpMaxMin_Sse', 'OpMovAp_Sse',
            'Helper_CmpPD', 'Helper_CmpSD', 'Helper_CmpPS', 'Helper_CmpSS',
        ],
    },
    'sse_cvt': {
        'desc': 'SSE/SSE2 Type Conversions',
        'functions': [
            'OpCvt_2A', 'OpCvt_2C', 'OpCvt_5A', 'OpCvt_5B', 'OpCvt_E6',
        ],
    },
    'sse_mov': {
        'desc': 'SSE/SSE2 Data Movement',
        'functions': [
            'OpMov_Sse_Load', 'OpMov_Sse_Store',
            'OpMovd_Load', 'OpMovd_Store',
            'OpMovq_Load', 'OpMovq_Store',
        ],
    },
    'fpu': {
        'desc': 'FPU Instructions',
        'functions': [
            'OpFpu_D8', 'OpFpu_D9', 'OpFpu_DA', 'OpFpu_DB',
            'OpFpu_DC', 'OpFpu_DD', 'OpFpu_DE', 'OpFpu_DF',
        ],
    },
    'data_mov': {
        'desc': 'Basic Data Movement',
        'functions': [
            'OpMov_EvGv', 'OpMov_GvEv', 'OpMov_RegImm', 'OpMov_RegImm8',
            'OpMov_EvIz', 'OpMov_Moffs_Load', 'OpMov_Moffs_Store',
            'OpMovzx_Byte', 'OpMovzx_Word', 'OpMovsx_Byte', 'OpMovsx_Word',
            'OpLea', 'OpPush_Reg', 'OpPush_Imm', 'OpPop_Reg',
        ],
    },
    'alu': {
        'desc': 'Arithmetic & Logic',
        'functions': [
            'OpAdd_EbGb', 'OpAdd_EvGv', 'OpAdd_GbEb', 'OpAdd_GvEv',
            'OpAdc_EbGb', 'OpAdc_EvGv', 'OpAdc_GbEb', 'OpAdc_GvEv',
            'OpSub_EvGv',
            'OpAnd_EbGb', 'OpAnd_EvGv', 'OpAnd_GbEb', 'OpAnd_GvEv',
            'OpOr_EvGv',
            'OpXor_EvGv',
            'OpInc_Reg', 'OpDec_Reg',
        ],
    },
    'shift_bit': {
        'desc': 'Shifts & Bit Operations',
        'functions': [
            'Helper_Group2',
            'OpGroup2_EvIb', 'OpGroup2_Ev1', 'OpGroup2_EvCl',
            'OpBt_EvGv', 'OpBt_EvIb', 'OpBtr_EvGv', 'OpBsr_GvEv', 'OpBswap_Reg',
        ],
    },
    'muldiv': {
        'desc': 'Multiplication & Division',
        'functions': [
            'OpImul_GvEv', 'OpImul_GvEvIz',
        ],
    },
    'control': {
        'desc': 'Control Flow',
        'functions': [
            'OpJmp_Rel', 'OpJcc_Rel', 'OpCall_Rel',
            'OpRet', 'OpInt', 'OpInt3', 'OpHlt', 'OpNop', 'OpCmov_GvEv',
        ],
    },
    'compare': {
        'desc': 'Comparison & Test',
        'functions': [
            'OpCmp_EbGb', 'OpCmp_EvGv', 'OpCmp_GbEb', 'OpCmp_GvEv',
            'OpTest_EvGv',
        ],
    },
    'groups': {
        'desc': 'Instruction Groups & Misc',
        'functions': [
            'OpGroup1_EbIb', 'OpGroup1_EvIz',
            'OpGroup3_Ev', 'OpGroup4_Eb', 'OpGroup5_Ev', 'OpGroup9',
            'OpXadd_Rm_R', 'OpCdq', 'OpCwde', 'OpUd2', 'OpDecodeFault',
        ],
    },
    'helpers': {
        'desc': 'Shared Helper Functions',
        'functions': [
            'GetReg8', 'ReadModRM',
        ],
    },
}


def find_function_body(content: str, func_name: str) -> Optional[Tuple[int, int, str]]:
    """
    在C++代码中查找函数完整定义（包括模板）
    
    Returns:
        (start_pos, end_pos, full_text) 或 None
    """
    # 匹配函数签名（支持模板、返回类型等）
    # Pattern handles: void OpFoo(...), template<...> void OpFoo(...), simde__m128 Helper_Foo(...)
    patterns = [
        # Standard function
        rf'^(void|uint8_t|uint16_t|uint32_t|simde__m128[d]?)\s+{re.escape(func_name)}\s*\(',
        # Template function
        rf'^template\s*<[^>]+>\s*\n(void|uint8_t|uint16_t|uint32_t)\s+{re.escape(func_name)}\s*\(',
    ]
    
    for pattern in patterns:
        match = re.search(pattern, content, re.MULTILINE)
        if match:
            start_pos = match.start()
            
            # 找到函数体的开始 {
            brace_start = content.find('{', match.end())
            if brace_start == -1:
                continue
            
            # 括号匹配找到函数体结束
            brace_count = 1
            pos = brace_start + 1
            in_string = False
            in_char = False
            in_comment = False
            in_block_comment = False
            
            while pos < len(content) and brace_count > 0:
                char = content[pos]
                prev_char = content[pos-1] if pos > 0 else ''
                
                # 处理字符串和注释
                if not in_comment and not in_block_comment:
                    if char == '"' and prev_char != '\\':
                        in_string = not in_string
                    elif char == "'" and prev_char != '\\':
                        in_char = not in_char
                    elif char == '/' and pos + 1 < len(content):
                        if content[pos+1] == '/':
                            in_comment = True
                        elif content[pos+1] == '*':
                            in_block_comment = True
                
                if in_comment and char == '\n':
                    in_comment = False
                elif in_block_comment and char == '*' and pos + 1 < len(content) and content[pos+1] == '/':
                    in_block_comment = False
                    pos += 1  # Skip the '/'
                
                # 只在非字符串/注释中计算括号
                if not in_string and not in_char and not in_comment and not in_block_comment:
                    if char == '{':
                        brace_count += 1
                    elif char == '}':
                        brace_count -= 1
                
                pos += 1
            
            if brace_count == 0:
                # 找到完整函数
                end_pos = pos
                full_text = content[start_pos:end_pos]
                return (start_pos, end_pos, full_text)
    
    return None


def extract_includes(content: str) -> List[str]:
    """提取文件开头的 #include 语句"""
    includes = []
    for line in content.split('\n'):
        line = line.strip()
        if line.startswith('#include'):
            includes.append(line)
        elif line and not line.startswith('//') and not line.startswith('/*'):
            # 遇到非 include/注释的代码就停止
            if line != 'namespace x86emu {':
                break
    return includes


def main():
    # 路径设置
    src_dir = Path('src')
    ops_cpp = src_dir / 'ops.cpp'
    ops_dir = src_dir / 'ops'
    
    if not ops_cpp.exists():
        print(f"错误: {ops_cpp} 不存在")
        return 1
    
    # 读取原文件
    print(f"读取 {ops_cpp}...")
    content = ops_cpp.read_text(encoding='utf-8')
    original_includes = extract_includes(content)
    
    # 创建 ops 目录
    ops_dir.mkdir(exist_ok=True)
    print(f"创建目录: {ops_dir}")
    
    # 统计信息
    total_funcs = 0
    extracted_funcs = 0
    failed_funcs = []
    
    # 处理每个类别
    for category_name, category_info in CATEGORIES.items():
        print(f"\n{'='*60}")
        print(f"类别: {category_name} - {category_info['desc']}")
        print(f"{'='*60}")
        
        functions = category_info['functions']
        total_funcs += len(functions)
        
        # 提取函数体
        extracted_bodies = []
        func_signatures = []
        
        for func_name in functions:
            result = find_function_body(content, func_name)
            if result:
                start, end, body = result
                extracted_bodies.append(body)
                
                # 提取函数签名用于头文件
                sig_match = re.search(r'^[^{]*', body, re.DOTALL)
                if sig_match:
                    sig = sig_match.group(0).strip()
                    # 移除可能的注释
                    sig = re.sub(r'/\*.*?\*/', '', sig, flags=re.DOTALL)
                    sig = re.sub(r'//.*?$', '', sig, flags=re.MULTILINE)
                    func_signatures.append(sig)
                
                extracted_funcs += 1
                print(f"  ✓ {func_name}")
            else:
                failed_funcs.append((category_name, func_name))
                print(f"  ✗ {func_name} (未找到)")
        
        if not extracted_bodies:
            print(f"  跳过 {category_name}（无函数提取）")
            continue
        
        # 生成源文件
        source_file = ops_dir / f'ops_{category_name}.cpp'
        with open(source_file, 'w', encoding='utf-8') as f:
            f.write(f'// {category_info["desc"]}\n')
            f.write(f'// Auto-generated from ops.cpp refactoring\n\n')
            f.write('#include "../ops.h"\n')
            f.write('#include "../state.h"\n')
            f.write('#include "../exec_utils.h"\n')
            f.write('#include <simde/x86/sse.h>\n')
            f.write('\nnamespace x86emu {\n\n')
            f.write('\n\n'.join(extracted_bodies))
            f.write('\n\n} // namespace x86emu\n')
        
        print(f"  → {source_file} ({len(extracted_bodies)} 函数)")
        
        # 生成头文件
        header_file = ops_dir / f'ops_{category_name}.h'
        with open(header_file, 'w', encoding='utf-8') as f:
            guard = f'X86EMU_OPS_{category_name.upper()}_H'
            f.write(f'// {category_info["desc"]}\n')
            f.write(f'// Auto-generated from ops.cpp refactoring\n\n')
            f.write(f'#pragma once\n\n')
            f.write('#include "../common.h"\n')
            f.write('#include "../decoder.h"\n\n')
            f.write('namespace x86emu {\n\n')
            
            for sig in func_signatures:
                f.write(f'{sig};\n\n')
            
            f.write('} // namespace x86emu\n')
        
        print(f"  → {header_file}")
    
    # 生成汇总报告
    print(f"\n{'='*60}")
    print("提取完成!")
    print(f"{'='*60}")
    print(f"总函数数: {total_funcs}")
    print(f"成功提取: {extracted_funcs}")
    print(f"提取失败: {len(failed_funcs)}")
    
    if failed_funcs:
        print("\n失败列表:")
        for cat, func in failed_funcs:
            print(f"  - {cat}/{func}")
    
    # 生成主包含头文件
    main_header = ops_dir / 'all_ops.h'
    with open(main_header, 'w', encoding='utf-8') as f:
        f.write('// Include all modular op headers\n')
        f.write('#pragma once\n\n')
        for category_name in CATEGORIES.keys():
            f.write(f'#include "ops_{category_name}.h"\n')
    
    print(f"\n生成主头文件: {main_header}")
    
    return 0


if __name__ == '__main__':
    exit(main())
