using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Options;

namespace StoryForge.Engine.Secrets;

/// <summary>
/// Secrets as generic credentials in Windows Credential Manager, one entry per secret named
/// "&lt;prefix&gt;:&lt;name&gt;" (e.g. "StoryForgeX:lm-studio-api-token"), visible under
/// Control Panel › Credential Manager › Windows Credentials.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCredentialStore(IOptions<StoryForgeEngineOptions> options) : ISecretStore
{
    private const int GenericCredential = 1;      // CRED_TYPE_GENERIC
    private const int PersistLocalMachine = 2;    // CRED_PERSIST_LOCAL_MACHINE: this user, this machine
    private const int NotFound = 1168;            // ERROR_NOT_FOUND

    private string Target(string name) => $"{options.Value.CredentialTargetPrefix}:{name}";

    public string? Read(string name)
    {
        if (!CredRead(Target(name), GenericCredential, 0, out var handle))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == NotFound ? null : throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            return credential.CredentialBlobSize == 0
                ? null
                : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / sizeof(char));
        }
        finally
        {
            CredFree(handle);
        }
    }

    public void Write(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (!CredDelete(Target(name), GenericCredential, 0) && Marshal.GetLastPInvokeError() != NotFound)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            return;
        }

        var blob = Encoding.Unicode.GetBytes(value);
        var blobHandle = Marshal.AllocHGlobal(blob.Length);
        var targetHandle = Marshal.StringToHGlobalUni(Target(name));
        try
        {
            Marshal.Copy(blob, 0, blobHandle, blob.Length);
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = targetHandle,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobHandle,
                Persist = PersistLocalMachine,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Array.Clear(blob);
            Marshal.FreeHGlobal(blobHandle);
            Marshal.FreeHGlobal(targetHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref Credential credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr buffer);
}
