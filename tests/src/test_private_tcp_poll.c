#undef NDEBUG
#include <arpa/inet.h>
#include <assert.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

static int set_nonblock(int fd) {
    int flags = fcntl(fd, F_GETFL, 0);
    if (flags < 0) {
        return -1;
    }
    return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

int main(void) {
    printf("--- Testing private TCP poll readiness ---\n");

    int server_fd = socket(AF_INET, SOCK_STREAM, 0);
    if (server_fd < 0) {
        perror("socket(server)");
        return 1;
    }

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(19120);
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
    if (set_nonblock(server_fd) != 0) {
        perror("fcntl(server)");
        close(server_fd);
        return 1;
    }

    int client_fd = socket(AF_INET, SOCK_STREAM, 0);
    if (client_fd < 0) {
        perror("socket(client)");
        close(server_fd);
        return 1;
    }
    if (set_nonblock(client_fd) != 0) {
        perror("fcntl(client)");
        close(client_fd);
        close(server_fd);
        return 1;
    }

    int rc = connect(client_fd, (struct sockaddr*)&addr, sizeof(addr));
    if (rc != 0 && errno != EINPROGRESS) {
        perror("connect(client)");
        close(client_fd);
        close(server_fd);
        return 1;
    }

    struct pollfd fds[2];
    memset(fds, 0, sizeof(fds));
    fds[0].fd = server_fd;
    fds[0].events = POLLIN;
    fds[1].fd = client_fd;
    fds[1].events = POLLOUT;

    int server_ready = 0;
    int client_ready = 0;
    for (int i = 0; i < 4 && (!server_ready || !client_ready); ++i) {
        rc = poll(fds, 2, 2000);
        if (rc <= 0) {
            perror("poll(connect)");
            close(client_fd);
            close(server_fd);
            return 1;
        }
        if ((fds[0].revents & POLLIN) != 0)
            server_ready = 1;
        if ((fds[1].revents & POLLOUT) != 0)
            client_ready = 1;
    }

    assert(server_ready);
    assert(client_ready);
    printf("Connect readiness observed: server=0x%x client=0x%x\n", fds[0].revents, fds[1].revents);

    int accepted_fd = accept(server_fd, NULL, NULL);
    if (accepted_fd < 0) {
        perror("accept(server)");
        close(client_fd);
        close(server_fd);
        return 1;
    }

    const char* payload = "poll-private-net";
    ssize_t sent = send(client_fd, payload, strlen(payload), 0);
    if (sent < 0) {
        perror("send(client)");
        close(accepted_fd);
        close(client_fd);
        close(server_fd);
        return 1;
    }

    struct pollfd read_fd;
    memset(&read_fd, 0, sizeof(read_fd));
    read_fd.fd = accepted_fd;
    read_fd.events = POLLIN;

    rc = poll(&read_fd, 1, 2000);
    if (rc <= 0) {
        perror("poll(read)");
        close(accepted_fd);
        close(client_fd);
        close(server_fd);
        return 1;
    }

    assert((read_fd.revents & POLLIN) != 0);
    printf("Read readiness observed: accepted=0x%x\n", read_fd.revents);

    char buf[64];
    ssize_t received = recv(accepted_fd, buf, sizeof(buf) - 1, 0);
    if (received < 0) {
        perror("recv(server)");
        close(accepted_fd);
        close(client_fd);
        close(server_fd);
        return 1;
    }
    buf[received] = '\0';
    assert(strcmp(buf, "poll-private-net") == 0);

    close(accepted_fd);
    close(client_fd);
    close(server_fd);
    printf("Private TCP poll test PASSED!\n");
    return 0;
}
