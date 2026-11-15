/* Native Linux oracle: recvmsg msg_flags is output, msg_namelen is capacity. */
#define _GNU_SOURCE
#include <assert.h>
#include <errno.h>
#include <netinet/in.h>
#include <stdio.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>

int main(void)
{
    int receiver = socket(AF_INET, SOCK_DGRAM, 0);
    int sender = socket(AF_INET, SOCK_DGRAM, 0);
    assert(receiver >= 0 && sender >= 0);
    struct timeval timeout = {.tv_sec = 2};
    assert(setsockopt(receiver, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout)) == 0);
    struct sockaddr_in address = {.sin_family = AF_INET, .sin_addr.s_addr = htonl(INADDR_LOOPBACK)};
    socklen_t length = sizeof(address);
    assert(bind(receiver, (struct sockaddr *)&address, length) == 0);
    assert(getsockname(receiver, (struct sockaddr *)&address, &length) == 0);
    char data[8];
    char name[4096];
    struct iovec iov = {.iov_base = data, .iov_len = sizeof(data)};
    for (int large = 0; large < 2; large++) {
        for (int input_flags = 0; input_flags < 2; input_flags++) {
            assert(sendto(sender, "z", 1, 0, (struct sockaddr *)&address, length) == 1);
            struct msghdr message = {.msg_name = name, .msg_namelen = large ? sizeof(name) : 128,
                .msg_iov = &iov, .msg_iovlen = 1, .msg_flags = input_flags ? MSG_PEEK | MSG_OOB : 0};
            errno = 0;
            ssize_t result = recvmsg(receiver, &message, 0);
            int error = errno;
            fprintf(stderr, "capacity=%d input_flags=%d -> result=%ld errno=%d output_name_length=%d output_flags=%d\n",
                large ? 4096 : 128, input_flags ? MSG_PEEK | MSG_OOB : 0,
                (long)result, error, message.msg_namelen, message.msg_flags);
            assert(result == 1 && data[0] == 'z' && message.msg_namelen == sizeof(address));
            assert(message.msg_flags == 0);
            assert(recv(receiver, data, sizeof(data), MSG_DONTWAIT) == -1 && errno == EAGAIN);
        }
    }
    assert(close(sender) == 0 && close(receiver) == 0);
    return 0;
}
