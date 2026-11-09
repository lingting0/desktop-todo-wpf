using System;
using System.Runtime.InteropServices;

namespace DesktopTodo.Config;

/// <summary>
/// 应用密码存入 Windows 凭据管理器，config.json 中不再出现任何明文密钥。
/// 使用 Win32 P/Invoke，保持项目「零 NuGet 依赖」。
/// </summary>
internal static class CredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const string TargetPrefix = "DesktopTodo/Nextcloud/";

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FileTime LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credential);

    /// <summary>按服务器地址 + 用户名区分，避免多账号互相覆盖。</summary>
    public static string BuildTarget(string url, string username)
    {
        var host = "server";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            host = uri.Host + "-" + uri.Port;
        var user = string.IsNullOrWhiteSpace(username) ? "anonymous" : username.Trim();
        return TargetPrefix + host + "/" + user;
    }

    public static bool Save(string target, string username, string secret)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(secret)) return false;

        var targetPtr = Marshal.StringToHGlobalUni(target);
        var userPtr = Marshal.StringToHGlobalUni(username ?? "");
        var blobPtr = Marshal.StringToHGlobalUni(secret);
        try
        {
            var cred = new NativeCredential
            {
                Flags = 0,
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                UserName = userPtr,
                CredentialBlob = blobPtr,
                CredentialBlobSize = (uint)(secret.Length * 2),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                Comment = IntPtr.Zero,
                TargetAlias = IntPtr.Zero,
            };
            return CredWrite(ref cred, 0);
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public static string? Load(string target)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<NativeCredential>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize < 2) return null;
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        catch
        {
            return null;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static void Delete(string target)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { CredDelete(target, CRED_TYPE_GENERIC, 0); }
        catch { /* 凭据不存在时无需处理 */ }
    }
}
