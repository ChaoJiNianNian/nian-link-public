using System.Security.Cryptography;
using DesktopToolbox.DeskLink;

namespace NianLink.Windows;

/// <summary>Windows protects stored trust for the current logged-in user.</summary>
public sealed class WindowsCredentialProtector : IMultiDeviceSecretProtector
{
    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
}
