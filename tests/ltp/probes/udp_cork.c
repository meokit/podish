/* Native Linux oracle for the branch's MSG_MORE overflow regression. */
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
    static char data[65000];
    int receiver = socket(AF_INET, SOCK_DGRAM, 0);
    int sender = socket(AF_INET, SOCK_DGRAM, 0);
    assert(receiver >= 0 && sender >= 0);
    struct sockaddr_in address = {.sin_family = AF_INET, .sin_addr.s_addr = htonl(INADDR_LOOPBACK)};
    socklen_t size = sizeof(address);
    struct timeval timeout = {.tv_sec = 2};
    assert(setsockopt(receiver, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout)) == 0);
    assert(bind(receiver, (struct sockaddr *)&address, size) == 0);
    assert(getsockname(receiver, (struct sockaddr *)&address, &size) == 0);
    assert(connect(sender, (struct sockaddr *)&address, size) == 0);
    assert(send(sender, data, sizeof(data), MSG_MORE) == sizeof(data));
    assert(send(sender, data, 1000, MSG_MORE) == -1 && errno == EMSGSIZE);
    assert(send(sender, "z", 1, 0) == 1);
    assert(recv(receiver, data, sizeof(data), 0) == 1 && data[0] == 'z');
    assert(close(sender) == 0 && close(receiver) == 0);
    puts("udp_cork: overflow -> EMSGSIZE, pending datagram discarded, next send delivers only new data");
    return 0;
}
