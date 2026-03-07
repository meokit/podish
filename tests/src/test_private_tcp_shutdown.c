#undef NDEBUG
#include <arpa/inet.h>
#include <assert.h>
#include <errno.h>
#include <netinet/in.h>
#include <poll.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

int main(void) {
    printf("--- Testing private TCP shutdown semantics ---\n");

    signal(SIGPIPE, SIG_IGN);

    int server_fd = socket(AF_INET, SOCK_STREAM, 0);
    int client_fd = socket(AF_INET, SOCK_STREAM, 0);
    if (server_fd < 0 || client_fd < 0) {
        perror("socket");
        return 1;
    }

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(19140);
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    if (bind(server_fd, (struct sockaddr*)&addr, sizeof(addr)) != 0) {
        perror("bind");
        return 1;
    }
    if (listen(server_fd, 1) != 0) {
        perror("listen");
        return 1;
    }
    if (connect(client_fd, (struct sockaddr*)&addr, sizeof(addr)) != 0) {
        perror("connect");
        return 1;
    }

    int accepted_fd = accept(server_fd, NULL, NULL);
    if (accepted_fd < 0) {
        perror("accept");
        return 1;
    }

    const char* payload = "shutdown-private";
    ssize_t sent = send(client_fd, payload, strlen(payload), 0);
    if (sent < 0) {
        perror("send(payload)");
        return 1;
    }
    if (shutdown(client_fd, SHUT_WR) != 0) {
        perror("shutdown(SHUT_WR)");
        return 1;
    }

    char buf[64];
    size_t total = 0;
    while (total < strlen(payload)) {
        ssize_t received = recv(accepted_fd, buf + total, sizeof(buf) - 1 - total, 0);
        if (received < 0) {
            perror("recv(payload)");
            return 1;
        }
        total += (size_t)received;
    }
    buf[total] = '\0';
    assert(strcmp(buf, payload) == 0);
    printf("Received payload before EOF\n");

    struct pollfd pfd;
    memset(&pfd, 0, sizeof(pfd));
    pfd.fd = accepted_fd;
    pfd.events = POLLIN;

    int rc = 0;
    for (int i = 0; i < 4; ++i) {
        rc = poll(&pfd, 1, 2000);
        if (rc <= 0) {
            perror("poll(hup)");
            return 1;
        }
        if ((pfd.revents & POLLHUP) != 0)
            break;
    }
    assert((pfd.revents & POLLHUP) != 0);
    printf("Observed revents=0x%x after peer shutdown\n", pfd.revents);

    ssize_t received = recv(accepted_fd, buf, sizeof(buf), 0);
    if (received < 0) {
        perror("recv(eof)");
        return 1;
    }
    assert(received == 0);
    printf("Observed EOF after peer shutdown\n");

    errno = 0;
    sent = send(client_fd, payload, strlen(payload), 0);
    assert(sent < 0);
    assert(errno == EPIPE);
    printf("Observed EPIPE after local shutdown\n");

    close(accepted_fd);
    close(client_fd);
    close(server_fd);
    printf("Private TCP shutdown test PASSED!\n");
    return 0;
}
