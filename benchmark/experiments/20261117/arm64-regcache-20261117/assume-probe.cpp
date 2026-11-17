#include <stdint.h>
struct S {
    uint32_t r[8];
};
extern __attribute__((preserve_none)) void next(S*, uint32_t);
__attribute__((preserve_none)) void f(S* s, uint32_t c) {
    __builtin_assume(s->r[0] == c);
    s->r[1] += s->r[0];
    [[clang::musttail]] return next(s, s->r[0]);
}
__attribute__((preserve_none)) void g(S* s, uint32_t c) {
    __builtin_assume(s->r[0] == c);
    s->r[0] += s->r[1];
    [[clang::musttail]] return next(s, s->r[0]);
}
