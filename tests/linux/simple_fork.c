#include <stdio.h>
#include <stdlib.h>
#include <sys/wait.h>
#include <unistd.h>

int main() {
  printf("Start\n");
  int pid = fork();
  if (pid < 0) {
    perror("fork");
    return 1;
  }
  if (pid == 0) {
    printf("Child running\n");
    _exit(0);
  } else {
    printf("Parent waiting\n");
    int status;
    waitpid(pid, &status, 0);
    printf("Parent done\n");
  }
  return 0;
}
