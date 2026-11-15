/* Native Linux oracle for socket option semantics used by the Podish netstack.
 * Records the target kernel's clamp/doubling rules and which options this backend
 * implements; it does not assert Podish behavior. */
#define _GNU_SOURCE
#include <arpa/inet.h>
#include <assert.h>
#include <errno.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

static void report(const char *name, int rc, int value, int error)
{
    printf("%-24s rc=%d value=%d errno=%d\n", name, rc, value, error);
}

static int get_int(int fd, int level, int opt)
{
    int value = -1;
    socklen_t len = sizeof(value);
    int rc = getsockopt(fd, level, opt, &value, &len);
    return rc == 0 ? value : -1;
}

int main(void)
{
    int udp = socket(AF_INET, SOCK_DGRAM, 0);
    int tcp = socket(AF_INET, SOCK_STREAM, 0);
    assert(udp >= 0 && tcp >= 0);
    int value, rc;

    value = 65536;
    rc = setsockopt(udp, SOL_SOCKET, SO_SNDBUF, &value, sizeof(value));
    report("SO_SNDBUF set 65536", rc, get_int(udp, SOL_SOCKET, SO_SNDBUF), errno);

    value = 1024;
    rc = setsockopt(udp, SOL_SOCKET, SO_RCVBUF, &value, sizeof(value));
    report("SO_RCVBUF set 1024", rc, get_int(udp, SOL_SOCKET, SO_RCVBUF), errno);

    value = (int)0xffffff00u;
    rc = setsockopt(udp, SOL_SOCKET, SO_SNDBUFFORCE, &value, sizeof(value));
    report("SO_SNDBUFFORCE neg", rc, get_int(udp, SOL_SOCKET, SO_SNDBUF), errno);

    value = 1;
    rc = setsockopt(tcp, IPPROTO_TCP, TCP_NODELAY, &value, sizeof(value));
    report("TCP_NODELAY", rc, get_int(tcp, IPPROTO_TCP, TCP_NODELAY), errno);

    rc = setsockopt(tcp, SOL_SOCKET, SO_KEEPALIVE, &value, sizeof(value));
    report("SO_KEEPALIVE", rc, get_int(tcp, SOL_SOCKET, SO_KEEPALIVE), errno);

    value = 100;
    rc = setsockopt(tcp, IPPROTO_TCP, TCP_KEEPIDLE, &value, sizeof(value));
    report("TCP_KEEPIDLE", rc, get_int(tcp, IPPROTO_TCP, TCP_KEEPIDLE), errno);

    value = 30;
    rc = setsockopt(tcp, IPPROTO_TCP, TCP_KEEPINTVL, &value, sizeof(value));
    report("TCP_KEEPINTVL", rc, get_int(tcp, IPPROTO_TCP, TCP_KEEPINTVL), errno);

    value = 5;
    rc = setsockopt(tcp, IPPROTO_TCP, TCP_KEEPCNT, &value, sizeof(value));
    report("TCP_KEEPCNT", rc, get_int(tcp, IPPROTO_TCP, TCP_KEEPCNT), errno);

    value = 1;
    rc = setsockopt(tcp, SOL_SOCKET, SO_OOBINLINE, &value, sizeof(value));
    report("SO_OOBINLINE", rc, get_int(tcp, SOL_SOCKET, SO_OOBINLINE), errno);

    rc = setsockopt(tcp, SOL_SOCKET, SO_REUSEPORT, &value, sizeof(value));
    report("SO_REUSEPORT", rc, get_int(tcp, SOL_SOCKET, SO_REUSEPORT), errno);

    struct linger linger = {.l_onoff = 1, .l_linger = 0};
    rc = setsockopt(tcp, SOL_SOCKET, SO_LINGER, &linger, sizeof(linger));
    struct linger back = {0};
    socklen_t back_len = sizeof(back);
    int get_rc = getsockopt(tcp, SOL_SOCKET, SO_LINGER, &back, &back_len);
    printf("%-24s set_rc=%d get_rc=%d onoff=%d linger=%d\n",
           "SO_LINGER {1,0}", rc, get_rc, back.l_onoff, back.l_linger);

    /* Multicast membership is per socket; TCP accepts it on Linux. */
    struct group_req group;
    memset(&group, 0, sizeof(group));
    group.gr_interface = 0;
    struct sockaddr_in *address = (struct sockaddr_in *)&group.gr_group;
    address->sin_family = AF_INET;
    address->sin_addr.s_addr = inet_addr("239.255.0.1");
    rc = setsockopt(tcp, IPPROTO_IP, MCAST_JOIN_GROUP, &group, sizeof(group));
    report("MCAST_JOIN (TCP)", rc, 0, errno);
    rc = setsockopt(tcp, IPPROTO_IP, MCAST_LEAVE_GROUP, &group, sizeof(group));
    report("MCAST_LEAVE (TCP)", rc, 0, errno);

    /* Live buffer resize is legal on Linux. */
    int listener = socket(AF_INET, SOCK_DGRAM, 0);
    struct sockaddr_in local = {.sin_family = AF_INET, .sin_addr.s_addr = htonl(INADDR_LOOPBACK)};
    assert(bind(listener, (struct sockaddr *)&local, sizeof(local)) == 0);
    value = 32768;
    rc = setsockopt(listener, SOL_SOCKET, SO_SNDBUF, &value, sizeof(value));
    report("SNDBUF after bind", rc, get_int(listener, SOL_SOCKET, SO_SNDBUF), errno);

    assert(close(udp) == 0 && close(tcp) == 0 && close(listener) == 0);
    return 0;
}
