using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CCNest.Core.Infrastructure;

/// <summary>
/// Stores secrets (proxy passwords, imported node definitions, controller secrets) encrypted
/// with Windows DPAPI in the current-user scope. Data is bound to the Windows account and
/// cannot be decrypted by other users or after copying the files to another machine.
/// Limitation: like every user-scope secret store, other programs running as the same
/// Windows user can ask DPAPI to decrypt it.
/// </summary>
public static class SecretStore
{
    public const string SecondStagePassword = "second-stage-password";
    public const string FirstStagePassword = "first-stage-password";
    public const string FirstStageNodeYaml = "first-stage-node";
    public const string ClashControllerSecret = "clash-controller-secret";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CCNest.SecretStore.v1");

    public static void Set(string key, string? value)
    {
        var path = PathFor(key);
        if (string.IsNullOrEmpty(value))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var protectedBytes = Protect(Encoding.UTF8.GetBytes(value));
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, path, overwrite: true);
        SecretRedactor.Register(value);
    }

    public static string? Get(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try
        {
            var value = Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(path)));
            SecretRedactor.Register(value);
            return value;
        }
        catch (Exception ex)
        {
            Log.Error("SecretStore", $"无法解密凭据 {key}（可能来自其他 Windows 账户）", ex);
            return null;
        }
    }

    public static bool Has(string key) => File.Exists(PathFor(key));

    private static string PathFor(string key)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            key = key.Replace(c, '_');
        return Path.Combine(AppPaths.SecretsDir, key + ".dpapi");
    }

    internal static byte[] Protect(byte[] data) => Crypt(data, protect: true);
    internal static byte[] Unprotect(byte[] data) => Crypt(data, protect: false);

    private static byte[] Crypt(byte[] data, bool protect)
    {
        var input = new DATA_BLOB();
        var entropy = new DATA_BLOB();
        var output = new DATA_BLOB();
        var hInput = GCHandle.Alloc(data, GCHandleType.Pinned);
        var hEntropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            input.cbData = data.Length;
            input.pbData = hInput.AddrOfPinnedObject();
            entropy.cbData = Entropy.Length;
            entropy.pbData = hEntropy.AddrOfPinnedObject();
            const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
            var ok = protect
                ? CryptProtectData(ref input, "CCNest", ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            hInput.Free();
            hEntropy.Free();
            if (output.pbData != IntPtr.Zero)
            {
                // Zero the plaintext copy owned by DPAPI before freeing it.
                for (var i = 0; i < output.cbData; i++) Marshal.WriteByte(output.pbData, i, 0);
                LocalFree(output.pbData);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
