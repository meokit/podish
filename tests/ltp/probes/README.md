# Native Linux semantic probes

These are independent i386 oracles, not edits to upstream LTP. Build with
`gcc -m32 -static -O2 -Wall -Wextra <source.c> -o <output>` in Linux/WSL.

- `mounts_poll.c` needs Linux root/CAP_SYS_ADMIN and mount namespace support.
  It unshares a private mount namespace before mutating mounts. It asserts that
  read/EOF/seek do not acknowledge mount notifications, Poll consumes them, and
  events=0 still reports ERR.
- `udp_cork.c` uses loopback UDP. It asserts that MSG_MORE overflow returns
  EMSGSIZE, discards the pending datagram, and does not contaminate the next send.
  It also checks that a cork keeps the first send's destination (even for a
  zero-byte first send), rather than redirecting the accumulated payload to the
  final sendto destination.
- `recvmsg_flags.c` uses loopback UDP. It asserts that name capacities 128/4096
  are accepted, the output name length is the actual sockaddr length, and input
  msg_flags=0/MSG_PEEK|MSG_OOB does not alter receive behavior. Receives wait with
  a two-second socket timeout so asynchronous native packet arrival is not
  mistaken for a semantic failure.

Observed outputs and related C# regressions are recorded in
`../NET_IO_BRANCH_REVIEW.md`. Native PASS does not imply every surrounding
Podish ABI or lifecycle path has been implemented.
