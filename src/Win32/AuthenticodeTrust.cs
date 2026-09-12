using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Nexus.Overlay.Win32;

/// <summary>The gate before running anything the overlay downloaded: a valid Authenticode chain (WinVerifyTrust, revocation checked) whose signer is Microsoft.</summary>
internal static unsafe class AuthenticodeTrust
{
    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const string MicrosoftSubject = "O=Microsoft Corporation";

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_WHOLECHAIN = 1;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_CHAIN = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public IntPtr hFile;
        public Guid* pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public WINTRUST_FILE_INFO* pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public char* pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    // Leading fields of CRYPT_PROVIDER_CERT; only pCert is read.
    [StructLayout(LayoutKind.Sequential)]
    private struct CRYPT_PROVIDER_CERT
    {
        public uint cbStruct;
        public IntPtr pCert;
    }

    [DllImport("wintrust.dll")]
    private static extern int WinVerifyTrust(IntPtr hwnd, Guid* pgActionID, WINTRUST_DATA* pWVTData);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);

    [DllImport("wintrust.dll")]
    private static extern CRYPT_PROVIDER_CERT* WTHelperGetProvCertFromChain(IntPtr pSgnr, uint idxCert);

    public static bool IsSignedByMicrosoft(string path)
    {
        var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
        fixed (char* p = path)
        {
            var file = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                pcwszFilePath = p,
                hFile = IntPtr.Zero,
                pgKnownSubject = null,
            };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = &file,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_CHAIN,
            };
            // INVALID_HANDLE_VALUE: no interactive user, never show UI.
            var status = WinVerifyTrust(new IntPtr(-1), &action, &data);
            try
            {
                if (status != 0)
                {
                    Log.Error($"authenticode: {path} rejected status=0x{status:X8}");
                    return false;
                }
                var subject = LeafSubject(data.hWVTStateData);
                if (subject is not null && subject.Contains(MicrosoftSubject, StringComparison.Ordinal)) return true;
                Log.Error($"authenticode: {path} signer is not Microsoft: {subject ?? "(unreadable)"}");
                return false;
            }
            finally
            {
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(new IntPtr(-1), &action, &data);
            }
        }
    }

    // The leaf certificate of the first signer, read from the verified chain
    // so the subject checked is the one WinVerifyTrust just validated.
    private static string? LeafSubject(IntPtr stateData)
    {
        var provData = WTHelperProvDataFromStateData(stateData);
        if (provData == IntPtr.Zero) return null;
        var signer = WTHelperGetProvSignerFromChain(provData, 0, false, 0);
        if (signer == IntPtr.Zero) return null;
        var cert = WTHelperGetProvCertFromChain(signer, 0);
        if (cert == null || cert->pCert == IntPtr.Zero) return null;
        using var x509 = new X509Certificate2(cert->pCert);
        return x509.Subject;
    }
}
