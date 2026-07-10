using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VoiceInput.Services;

internal static class AuthenticodeVerifier
{
    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static string? ExpectedCertificateSha256 =>
        typeof(AuthenticodeVerifier).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateSignerCertificateSha256")?.Value?
            .Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    public static bool VerifyPinnedPublisher(string filePath)
    {
        string? expected = ExpectedCertificateSha256;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(expected)) return false;
        if (WinVerifyTrust(filePath) != 0) return false;

        try
        {
#pragma warning disable SYSLIB0057 // The legacy API is the only BCL API that extracts an embedded PE signer certificate.
            using X509Certificate signer = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            using var signer2 = X509CertificateLoader.LoadCertificate(signer.Export(X509ContentType.Cert));
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                signer2.GetCertHash(HashAlgorithmName.SHA256));
        }
        catch
        {
            return false;
        }
    }

    private static int WinVerifyTrust(string filePath)
    {
        using var fileInfo = new WinTrustFileInfo(filePath);
        IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WinTrustData(fileInfoPtr);
            int result = WinVerifyTrust(IntPtr.Zero, ActionGenericVerifyV2, ref data);
            data.StateAction = WinTrustDataStateAction.Close;
            _ = WinVerifyTrust(IntPtr.Zero, ActionGenericVerifyV2, ref data);
            return result;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo : IDisposable
    {
        public uint StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        public IntPtr FilePath;
        public IntPtr FileHandle = IntPtr.Zero;
        public IntPtr KnownSubject = IntPtr.Zero;

        public WinTrustFileInfo(string filePath) => FilePath = Marshal.StringToCoTaskMemUni(filePath);
        public void Dispose()
        {
            if (FilePath == IntPtr.Zero) return;
            Marshal.FreeCoTaskMem(FilePath);
            FilePath = IntPtr.Zero;
            GC.SuppressFinalize(this);
        }

        ~WinTrustFileInfo() => Dispose();
    }

    private enum WinTrustDataUIChoice : uint { None = 2 }
    private enum WinTrustDataRevocationChecks : uint { None = 0 }
    private enum WinTrustDataChoice : uint { File = 1 }
    private enum WinTrustDataStateAction : uint { Ignore = 0, Verify = 1, Close = 2 }
    [Flags] private enum WinTrustDataProvFlags : uint { RevocationCheckChainExcludeRoot = 0x80 }
    private enum WinTrustDataUIContext : uint { Execute = 0 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SIPClientData;
        public WinTrustDataUIChoice UIChoice;
        public WinTrustDataRevocationChecks RevocationChecks;
        public WinTrustDataChoice UnionChoice;
        public IntPtr FileInfoPtr;
        public WinTrustDataStateAction StateAction;
        public IntPtr StateData;
        public IntPtr URLReference;
        public WinTrustDataProvFlags ProvFlags;
        public WinTrustDataUIContext UIContext;
        public IntPtr SignatureSettings;

        public WinTrustData(IntPtr fileInfoPtr)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = SIPClientData = IntPtr.Zero;
            UIChoice = WinTrustDataUIChoice.None;
            RevocationChecks = WinTrustDataRevocationChecks.None;
            UnionChoice = WinTrustDataChoice.File;
            FileInfoPtr = fileInfoPtr;
            StateAction = WinTrustDataStateAction.Verify;
            StateData = URLReference = IntPtr.Zero;
            ProvFlags = WinTrustDataProvFlags.RevocationCheckChainExcludeRoot;
            UIContext = WinTrustDataUIContext.Execute;
            SignatureSettings = IntPtr.Zero;
        }
    }
}
