#include <openssl/err.h>
#include <openssl/evp.h>
#include <openssl/provider.h>
#include <openssl/rsa.h>
#include <stdio.h>

void magic_debug(int arg1, int arg2, int arg3) {
    int ret;
    asm volatile("pushl %%ebx\n"
                 "movl %1, %%ebx\n"
                 "movl $888, %%eax\n"
                 "movl %2, %%ecx\n"
                 "movl %3, %%edx\n"
                 "int $0x80\n"
                 "movl %%eax, %0\n"
                 "popl %%ebx\n"
                 : "=r"(ret)
                 : "r"(arg1), "r"(arg2), "r"(arg3)
                 : "eax", "ecx", "edx", "memory");
}

int main(int argc, char** argv) {
    printf("Starting test_crypto\n");
    magic_debug(1, 0, 0); // Checkpoint 1: Start

    // OPENSSL_init_crypto(0, NULL);
    // magic_debug(2, 0, 0);

    OSSL_PROVIDER* prov = OSSL_PROVIDER_load(NULL, "default");
    if (!prov) {
        printf("Failed to load default provider\n");
        ERR_print_errors_fp(stderr);
    }
    magic_debug(3, (int)prov, 0);

    EVP_PKEY_CTX* ctx = EVP_PKEY_CTX_new_id(EVP_PKEY_RSA, NULL);
    if (!ctx) {
        printf("Failed to create context\n");
        ERR_print_errors_fp(stderr);
        return 1;
    }
    magic_debug(4, (int)ctx, 0); // Checkpoint 4: Context created

    if (EVP_PKEY_keygen_init(ctx) <= 0) {
        printf("Keygen init failed\n");
        return 1;
    }
    magic_debug(5, 0, 0); // Checkpoint 5: Keygen init

    if (EVP_PKEY_CTX_set_rsa_keygen_bits(ctx, 1024) <= 0) {
        printf("Set bits failed\n");
        return 1;
    }
    magic_debug(6, 0, 0); // Checkpoint 6: Set bits

    EVP_PKEY* pkey = NULL;
    if (EVP_PKEY_keygen(ctx, &pkey) <= 0) {
        printf("Keygen failed\n");
        return 1;
    }
    magic_debug(7, (int)pkey, 0); // Checkpoint 7: Key generated

    printf("Key generated successfully\n");
    EVP_PKEY_free(pkey);
    EVP_PKEY_CTX_free(ctx);
    OSSL_PROVIDER_unload(prov);
    return 0;
}
