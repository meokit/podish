
Package Overview for Berkeley SoftFloat Release 3e

John R. Hauser
2018 January 20

Berkeley SoftFloat is a software implementation of binary floating-point
that conforms to the IEEE Standard for Floating-Point Arithmetic.  SoftFloat
is distributed in the form of C source code.  Building the SoftFloat sources
generates a library file (typically "softfloat.a" or "libsoftfloat.a")
containing the floating-point subroutines.

The SoftFloat package is documented in the following files in the "doc"
subdirectory:

    SoftFloat.html          Documentation for using the SoftFloat functions.
    SoftFloat-source.html   Documentation for building SoftFloat.
    SoftFloat-history.html  History of the major changes to SoftFloat.

Other files in the package comprise the source code for SoftFloat.

Podish vendors a subset of release 3e. The binary32-64 directory contains
the sources needed by the AArch64 software floating-point path. Its NaN
helpers come from source/8086-SSE in the same release.

Upstream archive
https://www.jhauser.us/arithmetic/SoftFloat-3e.zip
SHA-256 21130ce885d35c1fe73fc1e1bf2244178167e05c6747cad5f450cc991714c746

