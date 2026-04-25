def generate_compressed_table():
    table = []
    # Index bits: OF(4) SF(3) ZF(2) PF(1) CF(0)
    for cond in range(16):
        val = 0
        for i in range(32):
            cf = (i >> 0) & 1
            pf = (i >> 1) & 1
            zf = (i >> 2) & 1
            sf = (i >> 3) & 1
            of = (i >> 4) & 1
            
            res = False
            if cond == 0: res = (of == 1)               # JO
            elif cond == 1: res = (of == 0)             # JNO
            elif cond == 2: res = (cf == 1)             # JB
            elif cond == 3: res = (cf == 0)             # JAE
            elif cond == 4: res = (zf == 1)             # JZ
            elif cond == 5: res = (zf == 0)             # JNZ
            elif cond == 6: res = (cf == 1 or zf == 1)  # JBE
            elif cond == 7: res = (cf == 0 and zf == 0) # JA
            elif cond == 8: res = (sf == 1)             # JS
            elif cond == 9: res = (sf == 0)             # JNS
            elif cond == 10: res = (pf == 1)            # JP
            elif cond == 11: res = (pf == 0)            # JNP
            elif cond == 12: res = (sf != of)           # JL
            elif cond == 13: res = (sf == of)           # JGE
            elif cond == 14: res = (zf == 1 or sf != of)# JLE
            elif cond == 15: res = (zf == 0 and sf == of)# JG
            
            if res:
                val |= (1 << i)
        table.append(val)
    return table

if __name__ == "__main__":
    table = generate_compressed_table()
    print("static const uint32_t g_ConditionLUT[16] = {")
    for i, val in enumerate(table):
        print(f"    0x{val:08X}, // cond {i}")
    print("};")