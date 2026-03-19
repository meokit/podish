# libdrm Event Handling

Source:

- [drmHandleEvent(3)](https://manpages.debian.org/testing/libdrm-dev/drmHandleEvent.3.en.html)

## What It Says

- `drmHandleEvent()` is called after the DRM fd becomes readable.
- It consumes pending events from the fd.
- It dispatches vblank and page-flip callbacks through `drmEventContext`.

## Key Implementation Takeaways

- If Fiberish supports page flips or vblank-like notifications, the fd must become readable when an event is pending.
- The event payload must match libdrm expectations closely enough for the read path to succeed.
- Immediate completion is possible, but the event semantics still need to look valid.

## Why This Matters For Fiberish

- Many client flows will not consider a commit complete until the event path works.
- Even if you start with immediate flips, the event contract should be designed early.

## Practical Checklist

- [ ] Decide whether to simulate vblank timing or complete flips immediately.
- [ ] Define the event queue format.
- [ ] Make the DRM fd poll/read path compatible with `drmHandleEvent()`.

