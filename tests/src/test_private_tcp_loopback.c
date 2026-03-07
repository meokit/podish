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
    printf("--- Testing private TCP loopback ---\n");

    int server_fd = socket(AF_INET, SOCK_STREAM, 0);
    if (server_fd < 0) {
        perror("socket(server)");
        return 1;
    }

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(19090);
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    if (bind(server_fd, (struct sockaddr*)&addr, sizeof(addr)) != 0) {
        perror("bind(server)");
        close(server_fd);
        return 1;
    }

    if (listen(server_fd, 1) != 0) {
        perror("listen(server)");
        close(server_fd);
        return 1;
    }
    printf("Listening on 127.0.0.1:19090\n");

    int client_fd = socket(AF_INET, SOCK_STREAM, 0);
    if (client_fd < 0) {
        perror("socket(client)");
        close(server_fd);
        return 1;
    }

    if (connect(client_fd, (struct sockaddr*)&addr, sizeof(addr)) != 0) {
        perror("connect(client)");
        close(client_fd);
        close(server_fd);
        return 1;
    }

    const char* payload = "hello-private-net";
    ssize_t sent = send(client_fd, payload, strlen(payload), 0);
    if (sent < 0) {
        perror("send(client)");
        close(client_fd);
        close(server_fd);
        return 1;
    }

    printf("Client sent %zd bytes\n", sent);

    struct sockaddr_in peer;
    socklen_t peer_len = sizeof(peer);
    int accepted_fd = accept(server_fd, (struct sockaddr*)&peer, &peer_len);
    if (accepted_fd < 0) {
        perror("accept(server)");
        close(server_fd);
        return 1;
    }

    char buf[64];
    ssize_t received = recv(accepted_fd, buf, sizeof(buf) - 1, 0);
    if (received < 0) {
        perror("recv(server)");
        close(accepted_fd);
        close(server_fd);
        return 1;
    }
    buf[received] = '\0';

    printf("Server received %zd bytes: %s\n", received, buf);
    assert(strcmp(buf, "hello-private-net") == 0);

    close(accepted_fd);
    close(client_fd);
    close(server_fd);
    printf("Private TCP loopback test PASSED!\n");
    return 0;
}
