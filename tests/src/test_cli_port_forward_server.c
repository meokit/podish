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

static int listen_on_any(int port) {
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) {
        perror("socket");
        return -1;
    }

    int yes = 1;
    if (setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof(yes)) != 0) {
        perror("setsockopt(SO_REUSEADDR)");
        close(fd);
        return -1;
    }

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons((uint16_t)port);
    addr.sin_addr.s_addr = htonl(INADDR_ANY);

    if (bind(fd, (struct sockaddr*)&addr, sizeof(addr)) != 0) {
        perror("bind");
        close(fd);
        return -1;
    }

    if (listen(fd, 4) != 0) {
        perror("listen");
        close(fd);
        return -1;
    }

    return fd;
}

static int handle_connection(int listen_fd, int port) {
    int client_fd = accept(listen_fd, NULL, NULL);
    if (client_fd < 0) {
        perror("accept");
        return 1;
    }

    char buf[128];
    ssize_t n = recv(client_fd, buf, sizeof(buf) - 1, 0);
    if (n < 0) {
        perror("recv");
        close(client_fd);
        return 1;
    }
    if (n > 0) {
        buf[n] = '\0';

        char reply[160];
        int written = snprintf(reply, sizeof(reply), "ACK:%d:%s", port, buf);
        assert(written > 0 && written < (int)sizeof(reply));

        if (send(client_fd, reply, (size_t)written, 0) < 0) {
            perror("send");
            close(client_fd);
            return 1;
        }
    }

    printf("HANDLED:%d\n", port);
    fflush(stdout);

    close(client_fd);
    return 0;
}

int main(int argc, char** argv) {
    if (argc != 2 && argc != 3) {
        fprintf(stderr, "usage: %s <port1> [port2]\n", argv[0]);
        return 2;
    }

    int port1 = atoi(argv[1]);
    int port2 = argc == 3 ? atoi(argv[2]) : 0;

    int fd1 = listen_on_any(port1);
    if (fd1 < 0) {
        return 1;
    }

    int fd2 = -1;
    if (port2 != 0) {
        fd2 = listen_on_any(port2);
        if (fd2 < 0) {
            close(fd1);
            return 1;
        }
    }

    printf("READY\n");
    fflush(stdout);

    int rc = handle_connection(fd1, port1);
    if (rc != 0) {
        close(fd1);
        if (fd2 >= 0)
            close(fd2);
        return rc;
    }

    if (fd2 >= 0) {
        rc = handle_connection(fd2, port2);
        if (rc != 0) {
            close(fd1);
            close(fd2);
            return rc;
        }
    }

    close(fd1);
    if (fd2 >= 0)
        close(fd2);

    printf("DONE\n");
    fflush(stdout);
    return 0;
}
