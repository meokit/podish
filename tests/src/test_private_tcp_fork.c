#undef NDEBUG
#include <arpa/inet.h>
#include <assert.h>
#include <errno.h>
#include <netinet/in.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/wait.h>
#include <unistd.h>

int main(void) {
    printf("--- Testing private TCP forked loopback ---\n");

    int server_fd = socket(AF_INET, SOCK_STREAM, 0);
    if (server_fd < 0) {
        perror("socket(server)");
        return 1;
    }

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(19110);
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

    pid_t pid = fork();
    if (pid < 0) {
        perror("fork");
        close(server_fd);
        return 1;
    }

    if (pid == 0) {
        int client_fd = socket(AF_INET, SOCK_STREAM, 0);
        if (client_fd < 0) {
            perror("socket(child)");
            _exit(2);
        }

        if (connect(client_fd, (struct sockaddr*)&addr, sizeof(addr)) != 0) {
            perror("connect(child)");
            close(client_fd);
            _exit(3);
        }

        const char* payload = "hello-from-child";
        ssize_t sent = send(client_fd, payload, strlen(payload), 0);
        if (sent < 0) {
            perror("send(child)");
            close(client_fd);
            _exit(4);
        }

        printf("Child sent %zd bytes\n", sent);
        fflush(stdout);
        close(client_fd);
        _exit(0);
    }

    int accepted_fd = accept(server_fd, NULL, NULL);
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

    int status = 0;
    if (waitpid(pid, &status, 0) < 0) {
        perror("waitpid");
        close(accepted_fd);
        close(server_fd);
        return 1;
    }

    if (!WIFEXITED(status) || WEXITSTATUS(status) != 0) {
        fprintf(stderr, "child failed: status=%d\n", status);
        close(accepted_fd);
        close(server_fd);
        return 1;
    }

    printf("Parent received %zd bytes: %s\n", received, buf);
    assert(strcmp(buf, "hello-from-child") == 0);

    close(accepted_fd);
    close(server_fd);
    printf("Private TCP fork test PASSED!\n");
    return 0;
}
