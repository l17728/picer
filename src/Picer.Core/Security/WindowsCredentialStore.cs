using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Picer.Core.Security;

public static class PicerCredentialTargets
{
    public const string Bailian = "Picer/CloudVision/Bailian";
    public const string Zhipu = "Picer/CloudVision/Zhipu";
    public const string WebDav = "Picer/Storage/WebDAV";
    public static string AiProvider(string providerId) => $"Picer/AI/{providerId.Trim()}";
}

public interface ICredentialStore
{
    string? Read(string target);
    void Write(string target, string secret);
    void Delete(string target);
}

public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint GenericCredential = 1;
    private const uint LocalMachinePersistence = 2;

    public string? Read(string target)
    {
        if (!CredRead(target, GenericCredential, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0 ? ""
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally { CredFree(pointer); }
    }

    public void Write(string target, string secret)
    {
        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var credential = new Credential { Type = GenericCredential, TargetName = target,
                CredentialBlobSize = (uint)(secret.Length * 2), CredentialBlob = blob,
                Persist = LocalMachinePersistence, UserName = Environment.UserName };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    public void Delete(string target)
    {
        if (!CredDelete(target, GenericCredential, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new Win32Exception(error);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags; public uint Type; public string TargetName; public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist;
        public uint AttributeCount; public IntPtr Attributes; public string? TargetAlias; public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
