/* Native Linux oracle for MSG_MORE overflow and first-destination ownership. */
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

    int other = socket(AF_INET, SOCK_DGRAM, 0);
    int unconnected = socket(AF_INET, SOCK_DGRAM, 0);
    assert(other >= 0 && unconnected >= 0);
    struct sockaddr_in other_address = {.sin_family = AF_INET, .sin_addr.s_addr = htonl(INADDR_LOOPBACK)};
    assert(bind(other, (struct sockaddr *)&other_address, sizeof(other_address)) == 0);
    size = sizeof(other_address);
    assert(getsockname(other, (struct sockaddr *)&other_address, &size) == 0);
    for (int empty = 0; empty <= 1; empty++) {
        int first_length = empty ? 0 : 1;
        assert(sendto(unconnected, "A", first_length, MSG_MORE,
                      (struct sockaddr *)&address, sizeof(address)) == first_length);
        assert(sendto(unconnected, "B", 1, 0,
                      (struct sockaddr *)&other_address, sizeof(other_address)) == 1);
        assert(recv(receiver, data, sizeof(data), 0) == first_length + 1);
        assert(data[first_length] == 'B' && (empty || data[0] == 'A'));
        assert(recv(other, data, sizeof(data), MSG_DONTWAIT) == -1 && errno == EAGAIN);
    }
    assert(close(other) == 0 && close(unconnected) == 0);
    assert(close(sender) == 0 && close(receiver) == 0);
    puts("udp_cork: overflow -> EMSGSIZE, pending datagram discarded, next send delivers only new data");
    puts("udp_cork: first destination wins, including a zero-byte first MSG_MORE send");
    return 0;
}
