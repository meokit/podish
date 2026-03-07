#undef NDEBUG
#include <arpa/inet.h>
#include <assert.h>
#include <errno.h>
#include <netinet/in.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

int main(void) {
    printf("--- Testing private connected UDP ---\n");

    int server_fd = socket(AF_INET, SOCK_DGRAM, 0);
    int client_fd = socket(AF_INET, SOCK_DGRAM, 0);
    if (server_fd < 0 || client_fd < 0) {
        perror("socket");
        return 1;
    }

    struct sockaddr_in server_addr;
    memset(&server_addr, 0, sizeof(server_addr));
    server_addr.sin_family = AF_INET;
    server_addr.sin_port = htons(19230);
    server_addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    struct sockaddr_in client_addr;
    memset(&client_addr, 0, sizeof(client_addr));
    client_addr.sin_family = AF_INET;
    client_addr.sin_port = htons(19231);
    client_addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    if (bind(server_fd, (struct sockaddr*)&server_addr, sizeof(server_addr)) != 0) {
        perror("bind(server)");
        return 1;
    }
    if (bind(client_fd, (struct sockaddr*)&client_addr, sizeof(client_addr)) != 0) {
        perror("bind(client)");
        return 1;
    }

    if (connect(client_fd, (struct sockaddr*)&server_addr, sizeof(server_addr)) != 0) {
        perror("connect(client)");
        return 1;
    }

    const char* outbound = "connected-udp";
    ssize_t sent = send(client_fd, outbound, strlen(outbound), 0);
    if (sent < 0) {
        perror("send(client)");
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
    assert(strcmp(buf, "connected-udp") == 0);

    if (connect(server_fd, (struct sockaddr*)&peer, peer_len) != 0) {
        perror("connect(server)");
        return 1;
    }

    const char* reply = "udp-reply";
    sent = send(server_fd, reply, strlen(reply), 0);
    if (sent < 0) {
        perror("send(server)");
        return 1;
    }

    received = recv(client_fd, buf, sizeof(buf) - 1, 0);
    if (received < 0) {
        perror("recv(client)");
        return 1;
    }
    buf[received] = '\0';
    assert(strcmp(buf, "udp-reply") == 0);

    printf("Connected UDP request/reply PASSED!\n");
    close(client_fd);
    close(server_fd);
    return 0;
}
