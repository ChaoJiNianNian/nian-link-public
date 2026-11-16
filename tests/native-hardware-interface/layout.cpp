#define _WIN32_WINNT 0x0a00
#include <stdio.h>
#include <stddef.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>

int main(void)
{
    MIB_IF_ROW2 row{};
    row.InterfaceAndOperStatusFlags.HardwareInterface = 1;
    printf("size=%zu index=%zu guid=%zu flags=%zu hardware=%u\n",
        sizeof(row), offsetof(MIB_IF_ROW2, InterfaceIndex), offsetof(MIB_IF_ROW2, InterfaceGuid),
        offsetof(MIB_IF_ROW2, InterfaceAndOperStatusFlags),
        *(const unsigned char *)&row.InterfaceAndOperStatusFlags);
    return 0;
}
