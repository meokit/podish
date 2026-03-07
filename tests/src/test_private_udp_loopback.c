#undef NDEBUG
#include <arpa/inet.h>
#include <assert.h>
#include <errno.h>
#include <netinet/in.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

int main(void) {
    printf("--- Testing private UDP loopback ---\n");

    int server_fd = socket(AF_INET, SOCK_DGRAM, 0);
    int client_fd = socket(AF_INET, SOCK_DGRAM, 0);
    if (server_fd < 0 || client_fd < 0) {
        perror("socket");
        return 1;
    }

    struct sockaddr_in server_addr;
    memset(&server_addr, 0, sizeof(server_addr));
    server_addr.sin_family = AF_INET;
    server_addr.sin_port = htons(19220);
    server_addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    struct sockaddr_in client_addr;
    memset(&client_addr, 0, sizeof(client_addr));
    client_addr.sin_family = AF_INET;
    client_addr.sin_port = htons(19221);
    client_addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    if (bind(server_fd, (struct sockaddr*)&server_addr, sizeof(server_addr)) != 0) {
        perror("bind(server)");
        return 1;
    }
    if (bind(client_fd, (struct sockaddr*)&client_addr, sizeof(client_addr)) != 0) {
        perror("bind(client)");
        return 1;
    }

    const char* payload = "hello-udp-private";
    ssize_t sent = sendto(client_fd, payload, strlen(payload), 0, (struct sockaddr*)&server_addr, sizeof(server_addr));
    if (sent < 0) {
        perror("sendto(client)");
        return 1;
    }

    struct pollfd pfd;
    memset(&pfd, 0, sizeof(pfd));
    pfd.fd = server_fd;
    pfd.events = POLLIN;
    int prc = poll(&pfd, 1, 2000);
    if (prc <= 0) {
        perror("poll(server)");
        return 1;
    }

    char buf[64];
    struct sockaddr_in peer;
    socklen_t peer_len = sizeof(peer);
    ssize_t received = recvfrom(server_fd, buf, sizeof(buf) - 1, 0, (struct sockaddr*)&peer, &peer_len);
    if (received < 0) {
        perror("recvfrom(server)");
        return 1;
    }
    buf[received] = '\0';

    printf("Server received %zd bytes: %s\n", received, buf);
    assert(strcmp(buf, "hello-udp-private") == 0);
    assert(ntohl(peer.sin_addr.s_addr) == INADDR_LOOPBACK);
    assert(ntohs(peer.sin_port) == 19221);

    close(client_fd);
    close(server_fd);
    printf("Private UDP loopback test PASSED!\n");
    return 0;
}
