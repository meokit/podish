#undef NDEBUG
#include <arpa/inet.h>
#include <assert.h>
#include <errno.h>
#include <netinet/in.h>
#include <netinet/ip.h>
#include <netinet/ip_icmp.h>
#include <poll.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

static uint16_t checksum16(const void* data, size_t len) {
    const uint8_t* bytes = (const uint8_t*)data;
    uint32_t sum = 0;

    while (len > 1) {
        sum += (uint16_t)((bytes[0] << 8) | bytes[1]);
        bytes += 2;
        len -= 2;
    }

    if (len == 1) {
        sum += (uint16_t)(bytes[0] << 8);
    }

    while ((sum >> 16) != 0) {
        sum = (sum & 0xffffu) + (sum >> 16);
    }

    return (uint16_t)(~sum);
}

int main(void) {
    printf("--- Testing ICMP ping socket ---\n");

    int fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_ICMP);
    if (fd < 0) {
        perror("socket(AF_INET, SOCK_DGRAM, IPPROTO_ICMP)");
        return 1;
    }
    printf("Created ping socket fd=%d\n", fd);

    int so_type = 0;
    socklen_t so_type_len = sizeof(so_type);
    int rc = getsockopt(fd, SOL_SOCKET, SO_TYPE, &so_type, &so_type_len);
    if (rc != 0) {
        perror("getsockopt(SO_TYPE)");
        close(fd);
        return 1;
    }
    assert(so_type == SOCK_DGRAM);
    printf("Verified SO_TYPE=SOCK_DGRAM\n");

    struct sockaddr_in dst;
    memset(&dst, 0, sizeof(dst));
    dst.sin_family = AF_INET;
    dst.sin_port = 0;
    dst.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    uint8_t packet[64];
    memset(packet, 0, sizeof(packet));

    struct icmphdr* icmp = (struct icmphdr*)packet;
    icmp->type = ICMP_ECHO;
    icmp->code = 0;
    icmp->un.echo.id = htons(0x1234);
    icmp->un.echo.sequence = htons(1);

    const char* payload = "fiberish-ping";
    const size_t payload_len = strlen(payload);
    memcpy(packet + sizeof(struct icmphdr), payload, payload_len);

    const size_t packet_len = sizeof(struct icmphdr) + payload_len;
    icmp->checksum = htons(checksum16(packet, packet_len));

    ssize_t sent = sendto(fd, packet, packet_len, 0, (struct sockaddr*)&dst, sizeof(dst));
    if (sent < 0) {
        perror("sendto");
        close(fd);
        return 1;
    }
    assert((size_t)sent == packet_len);
    printf("Sent ICMP echo request (%zd bytes)\n", sent);

    struct pollfd pfd;
    memset(&pfd, 0, sizeof(pfd));
    pfd.fd = fd;
    pfd.events = POLLIN;
    rc = poll(&pfd, 1, 1000);
    if (rc < 0) {
        perror("poll");
        close(fd);
        return 1;
    }
    if (rc == 0) {
        fprintf(stderr, "poll timed out waiting for ICMP reply\n");
        close(fd);
        return 1;
    }

    uint8_t recv_buf[256];
    struct sockaddr_in src;
    socklen_t src_len = sizeof(src);
    ssize_t received = recvfrom(fd, recv_buf, sizeof(recv_buf), 0, (struct sockaddr*)&src, &src_len);
    if (received < 0) {
        perror("recvfrom");
        close(fd);
        return 1;
    }

    size_t icmp_offset = 0;
    if ((size_t)received >= sizeof(struct ip)) {
        const struct ip* iph = (const struct ip*)recv_buf;
        if (iph->ip_v == 4) {
            icmp_offset = (size_t)iph->ip_hl * 4u;
        }
    }

    assert((size_t)received >= icmp_offset + sizeof(struct icmphdr));
    const struct icmphdr* reply = (const struct icmphdr*)(recv_buf + icmp_offset);
    assert(reply->type == ICMP_ECHOREPLY);
    printf("Received ICMP echo reply (%zd bytes)\n", received);

    close(fd);
    printf("Ping socket test PASSED!\n");
    return 0;
}
