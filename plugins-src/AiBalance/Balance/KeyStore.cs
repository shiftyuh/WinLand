using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AiBalance.Balance;

/// <summary>
/// 密钥保存：每把密钥用 Windows DPAPI（当前用户作用域）加密成 base64，落在插件自己目录的 keys.dat。
/// 明文永远不进宿主 settings.json、不进 providers.json、不进日志；文件拷到别的用户/别的机器也解不开。
/// 解密失败（换了用户、文件损坏）就当作没有密钥，让用户重填，不会让插件起不来。
/// </summary>
public sealed class KeyStore
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private readonly string _path;
    private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);

    public KeyStore(string pluginDirectory)
    {
        _path = Path.Combine(pluginDirectory, "keys.dat");
        Load();
    }

    public string? Get(string providerId)
        => _keys.TryGetValue(providerId, out var key) && key.Length > 0 ? key : null;

    public void Set(string providerId, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            if (_keys.Remove(providerId)) Save();
            return;
        }

        _keys[providerId] = key.Trim();
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (stored == null) return;
            foreach (var (id, protectedValue) in stored)
            {
                var plain = Unprotect(protectedValue);
                if (!string.IsNullOrEmpty(plain)) _keys[id] = plain!;
            }
        }
        catch
        {
            // 文件损坏 / 换用户导致 DPAPI 解不开：当作没有密钥，别让插件初始化失败
        }
    }

    private void Save()
    {
        try
        {
            var stored = _keys.ToDictionary(pair => pair.Key, pair => Protect(pair.Value));
            var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json, new UTF8Encoding(false));
        }
        catch
        {
            // 写不进去也不能崩插件：内存里已经生效，只是重启后要重填
        }
    }

    private static string Protect(string plain)
    {
        var input = Encoding.UTF8.GetBytes(plain);
        var blob = ToBlob(input);
        try
        {
            if (!CryptProtectData(ref blob, "WinIsland ai-balance", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                throw new InvalidOperationException("CryptProtectData 失败");
            }

            try
            {
                return Convert.ToBase64String(FromBlob(output));
            }
            finally
            {
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            }
        }
        finally
        {
            if (blob.pbData != IntPtr.Zero) LocalFree(blob.pbData);
        }
    }

    private static string? Unprotect(string protectedBase64)
    {
        try
        {
            var data = Convert.FromBase64String(protectedBase64);
            var blob = ToBlob(data);
            try
            {
                if (!CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                {
                    return null;
                }

                try
                {
                    return Encoding.UTF8.GetString(FromBlob(output));
                }
                finally
                {
                    if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
                }
            }
            finally
            {
                if (blob.pbData != IntPtr.Zero) LocalFree(blob.pbData);
            }
        }
        catch
        {
            return null;
        }
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var blob = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        if (data.Length > 0) Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static byte[] FromBlob(DataBlob blob)
    {
        var bytes = new byte[blob.cbData];
        if (blob.cbData > 0) Marshal.Copy(blob.pbData, bytes, 0, blob.cbData);
        return bytes;
    }
}