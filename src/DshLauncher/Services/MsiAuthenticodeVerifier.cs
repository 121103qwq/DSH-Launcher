using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace DshLauncher.Services;

/// <summary>
/// 下载的 Node.js MSI 会以管理员权限执行；执行前必须确认文件带有完整
/// Authenticode 签名链，且签名者是 Node.js 官方发布者，避免下载源或镜像
/// 被污染时把任意 payload 直接交给 msiexec 提权安装。
/// </summary>
internal static class MsiAuthenticodeVerifier
{
    private static readonly Guid WinTrustActionGenericVerify2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;

    public static bool IsTrustedNodeInstaller(string filePath) =>
        HasValidSignature(filePath)
        && IsAllowedNodePublisher(ReadSignerCertificate(filePath));

    internal static bool HasValidSignature(string filePath)
    {
        try
        {
            var filePathPointer = Marshal.StringToHGlobalUni(filePath);
            var fileInfoPointer = IntPtr.Zero;
            try
            {
                var fileInfo = new WinTrustFileInfo
                {
                    cbSize = Marshal.SizeOf<WinTrustFileInfo>(),
                    pcwszFilePath = filePathPointer
                };
                fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

                var data = new WinTrustData
                {
                    cbSize = Marshal.SizeOf<WinTrustData>(),
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeNone,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = fileInfoPointer,
                    dwStateAction = WtdStateActionVerify
                };
                var actionId = WinTrustActionGenericVerify2;
                var verified = WinVerifyTrust(IntPtr.Zero, ref actionId, ref data) == 0;

                data.dwStateAction = WtdStateActionClose;
                WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);
                return verified;
            }
            finally
            {
                Marshal.FreeHGlobal(fileInfoPointer);
                Marshal.FreeHGlobal(filePathPointer);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or OutOfMemoryException
            or System.ComponentModel.Win32Exception)
        {
            // 验证基础设施本身失败时按不信任处理，提权前不放行。
            return false;
        }
    }

    internal static X509Certificate2? ReadSignerCertificate(string filePath)
    {
        try
        {
            return new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
        }
        catch
        {
            // 未签名或无法提取签名者的文件已经在链验证阶段被拒绝。
            return null;
        }
    }

    internal static bool IsAllowedNodePublisher(X509Certificate2? certificate)
    {
        if (certificate is null)
        {
            return false;
        }

        foreach (var publisher in new[] { "OpenJS Foundation", "Node.js Foundation", "Joyent" })
        {
            if (certificate.Subject.Contains(publisher, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public int cbSize;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public int cbSize;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }
}
