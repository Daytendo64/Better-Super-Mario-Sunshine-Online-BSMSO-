#include <SMS/macros.h>
#include <sdk.h>

// Gecko cutscene skip (NTSC-U): force TMovieDirector::direct() branches to succeed.
// 042B5EF4 38600001  -> li r3, 1 @ 0x802B5EF4
// 042B5E8C 38600001  -> li r3, 1 @ 0x802B5E8C
SMS_WRITE_32(SMS_PORT_REGION(0x802B5EF4, 0, 0, 0), 0x38600001);
SMS_WRITE_32(SMS_PORT_REGION(0x802B5E8C, 0, 0, 0), 0x38600001);

// 04142998 48000078  -> b +0x78 @ 0x80142998
SMS_WRITE_32(SMS_PORT_REGION(0x80142998, 0, 0, 0), 0x48000078);
