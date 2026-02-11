
import os
import re

# Paths to process
TARGET_DIR = "libfibercpu/ops"
EXTENSIONS = {".cpp"}

# Helper to find the matching closing parenthesis for a function call
def find_arguments(text, start_index):
    # start_index should be the index of the opening parenthesis '('
    if text[start_index] != '(':
        return None, None
    
    balance = 0
    for i in range(start_index, len(text)):
        char = text[i]
        if char == '(':
            balance += 1
        elif char == ')':
            balance -= 1
            if balance == 0:
                # Found the matching closing parenthesis
                # Return the content inside (exclusive) and the end index (inclusive)
                return text[start_index+1:i], i
    return None, None

def replace_calls(content, pattern_start, replacement_template):
    # Pattern start: e.g. "ReadModRM<([^>]+), true, true>"
    # We find all occurrences of pattern_start
    # Then we parse the arguments manually to handle nesting
    
    # We use a while loop to handle multiple occurrences and valid replacements
    # Since we are modifying strings, we should restart search or be careful with indices.
    # Simplest is to search from scratch until no more matches found.
    
    regex = re.compile(pattern_start + r'\s*\(')
    
    while True:
        match = regex.search(content)
        if not match:
            break
            
        # Get the match groups from the pattern
        groups = match.groups()
        
        # Find arguments
        # match.end() points to the character after '(', but our regex includes '(', so match.end()-1 is '('.
        # Wait, regex includes '('. So match.end() is after '('. 
        # Actually regex ends with '\('.
        
        open_paren_index = match.end() - 1
        args_content, close_paren_index = find_arguments(content, open_paren_index)
        
        if args_content is None:
            print(f"Error: Could not find matching parenthesis starting at {open_paren_index}")
            break
            
        # Construct replacement
        # replacement_template format: "ReadModRM<{}, OpOnTLBMiss::Restart>({})"
        # We need to format it with groups and args_content
        
        # We assume replacement_template uses python format syntax for groups: {0}, {1}... 
        # and has a placeholder for args: {args}
        
        # Let's standardize the interface.
        # template should be a function that takes (groups, args) -> string
        
        new_text = replacement_template(groups, args_content)
        
        # Replace in content
        # original text was: match.group(0) + args_content + ")"
        # But match.group(0) is the function name and open paren.
        # Entire match is: content[match.start() : close_paren_index + 1]
        
        content = content[:match.start()] + new_text + content[close_paren_index+1:]
        
    return content

def process_file(filepath):
    with open(filepath, 'r') as f:
        content = f.read()
    
    original_content = content

    # 1. ReadModRM
    # Pattern: ReadModRM<TYPE, true, true>(ARGS)
    content = replace_calls(
        content,
        r'ReadModRM<([^>]+),\s*true,\s*true>',
        lambda g, args: f"ReadModRM<{g[0]}, OpOnTLBMiss::Restart>({args})"
    )

    # 2. WriteModRM
    content = replace_calls(
        content,
        r'WriteModRM<([^>]+),\s*true,\s*true>',
        lambda g, args: f"WriteModRM<{g[0]}, OpOnTLBMiss::Retry>({args})"
    )

    # 3. Direct MMU Read (Restart)
    # Pattern: state->mmu.read<TYPE, true>(ARGS)
    # We need to capture the object name "state" or whatever.
    content = replace_calls(
        content,
        r'([a-zA-Z0-9_]+)->mmu\.read<([^>]+),\s*true>',
        lambda g, args: f"ReadMem<{g[1]}, OpOnTLBMiss::Restart>({g[0]}, {args})"
    )

    # 4. Direct MMU Write (Retry)
    content = replace_calls(
        content,
        r'([a-zA-Z0-9_]+)->mmu\.write<([^>]+),\s*true>',
        lambda g, args: f"WriteMem<{g[1]}, OpOnTLBMiss::Retry>({g[0]}, {args})"
    )

    # 5. Direct MMU Read (Blocking)
    content = replace_calls(
        content,
        r'([a-zA-Z0-9_]+)->mmu\.read<([^>]+),\s*false>',
        lambda g, args: f"ReadMem<{g[1]}, OpOnTLBMiss::Blocking>({g[0]}, {args})"
    )

    # 6. Direct MMU Write (Blocking)
    content = replace_calls(
        content,
        r'([a-zA-Z0-9_]+)->mmu\.write<([^>]+),\s*false>',
        lambda g, args: f"WriteMem<{g[1]}, OpOnTLBMiss::Blocking>({g[0]}, {args})"
    )


    if content != original_content:
        print(f"Modifying {filepath}")
        with open(filepath, 'w') as f:
            f.write(content)

def main():
    base_path = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    ops_dir = os.path.join(base_path, TARGET_DIR)

    if not os.path.exists(ops_dir):
        print(f"Directory not found: {ops_dir}")
        return

    for root, dirs, files in os.walk(ops_dir):
        for file in files:
            if any(file.endswith(ext) for ext in EXTENSIONS):
                filepath = os.path.join(root, file)
                process_file(filepath)

if __name__ == "__main__":
    main()
