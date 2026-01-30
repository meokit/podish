import random
import sys
import time
from runner import TestRunner
import struct

# Helper to pack random immediates
def imm8(): return random.randint(-128, 127) & 0xFF
def imm32(): return random.randint(-2147483648, 2147483647) & 0xFFFFFFFF
def reg32(): return random.choice(["eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi"])
def reg8(): return random.choice(["al", "cl", "dl", "bl", "ah", "ch", "dh", "bh"])
def mem32(): return f"dword [0x1000]" # Simplified memory

class Fuzzer:
    def __init__(self):
        self.runner = TestRunner()
        self.stats = {'success': 0, 'fail': 0}

    def generate_random_instruction(self):
        # Weighted choice of instruction templates
        templates = [
            # Group 1
            lambda: f"mov {reg32()}, {imm32()}",
            lambda: f"mov {reg32()}, {reg32()}",
            lambda: f"mov {reg32()}, {mem32()}",
            lambda: f"mov {mem32()}, {reg32()}",
            lambda: f"push {reg32()}",
            lambda: f"push {imm32()}",
            lambda: f"pop {reg32()}",
            lambda: f"lea {reg32()}, [{reg32()} + {imm8()}]",
            
            # Group 2
            lambda: f"add {reg32()}, {reg32()}",
            lambda: f"add {reg32()}, {imm32()}",
            lambda: f"sub {reg32()}, {reg32()}",
            lambda: f"sub {reg32()}, {imm32()}",
            lambda: f"xor {reg32()}, {reg32()}",
            lambda: f"and {reg32()}, {reg32()}",
            lambda: f"or {reg32()}, {reg32()}",
            lambda: f"inc {reg32()}",
            lambda: f"dec {reg32()}",
            
            # Group 2 Shift/Rotate
            lambda: f"shl {reg32()}, 1",
            lambda: f"shl {reg32()}, {random.randint(0, 31)}",
            lambda: f"shr {reg32()}, {random.randint(0, 31)}",
            lambda: f"sar {reg32()}, {random.randint(0, 31)}",
            lambda: f"rol {reg32()}, {random.randint(0, 31)}",
            lambda: f"ror {reg32()}, {random.randint(0, 31)}",
            
            # Extended
            lambda: f"movzx {reg32()}, {reg8()}",
            lambda: f"movsx {reg32()}, {reg8()}"
        ]
        
        # Pick one
        tmpl = random.choice(templates)
        return tmpl()

    def run_fuzz(self, iterations=100):
        print(f"[*] Starting Fuzzing for {iterations} iterations...")
        start_time = time.time()
        
        for i in range(iterations):
            # Generate a sequence of instructions
            # Length 5 to 20
            length = random.randint(5, 20)
            instrs = []
            
            # Setup registers with random initial values (via MOV)
            for r in ["eax", "ecx", "edx", "ebx", "esi", "edi", "ebp"]:
                instrs.append(f"mov {r}, {imm32()}")
            
            # Generate body
            for _ in range(length):
                instrs.append(self.generate_random_instruction())
                
            # Terminator
            instrs.append("hlt")
            
            asm = "\n".join(instrs)
            
            # Run Test
            # We don't verify specific regs, we just check against Unicorn (implicit in runner)
            test_name = f"Fuzz Case #{i+1}"
            
            # Runner prints output. We suppress it?
            # runner.run_test returns True/False
            # To suppress noise we could capture stdout?
            # For now let it print.
            
            print(f"\n--- Fuzz #{i+1} ---")
            # print(asm)
            
            try:
                success = self.runner.run_test(test_name, asm)
                if success:
                    self.stats['success'] += 1
                else:
                    self.stats['fail'] += 1
                    print(f"[-] Fuzz Failure on:\n{asm}")
                    break # Stop on first failure
            except Exception as e:
                self.stats['fail'] += 1
                print(f"[-] Exception: {e}")
                break
                
        end_time = time.time()
        print(f"\n[*] Fuzzing Complete. Success: {self.stats['success']}, Fail: {self.stats['fail']}")
        print(f"[*] Duration: {end_time - start_time:.2f}s")

if __name__ == "__main__":
    count = 10
    if len(sys.argv) > 1:
        count = int(sys.argv[1])
        
    fuzzer = Fuzzer()
    fuzzer.run_fuzz(count)
