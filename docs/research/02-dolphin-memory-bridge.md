# Dolphin Memory Bridge

Pattern from ww-multiplayer: launcher uses ReadProcessMemory/WriteProcessMemory on a fixed MEM1 mailbox.

- Address: `0x817FC000` (configurable)
- Size: 672 bytes
- Magic: `0x534D534F`

No Dolphin hooks or Netplay required.
