using System.Security.Cryptography;
using System.Text;

namespace Glint.Phase0.Core;

public sealed class DpapiKeyStore
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("Glint.Phase0.SQLCipher.v1");

    private readonly string _keyPath;

    public DpapiKeyStore(string keyPath)
    {
        _keyPath = Path.GetFullPath(keyPath);
    }

    public byte[] GetOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var protectedKey = File.ReadAllBytes(_keyPath);
            var existing = ProtectedData.Unprotect(
                protectedKey,
                Entropy,
                DataProtectionScope.CurrentUser);
            if (existing.Length != 32)
            {
                CryptographicOperations.ZeroMemory(existing);
                throw new InvalidDataException("DPAPI key payload is not 32 bytes.");
            }

            return existing;
        }

        var directory = Path.GetDirectoryName(_keyPath)
            ?? throw new InvalidOperationException("Key path has no parent directory.");
        Directory.CreateDirectory(directory);

        var key = RandomNumberGenerator.GetBytes(32);
        var protectedBytes = ProtectedData.Protect(
            key,
            Entropy,
            DataProtectionScope.CurrentUser);
        var temporaryPath = _keyPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4_096,
                       FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, _keyPath);
            }
            catch (IOException) when (File.Exists(_keyPath))
            {
                var winner = ProtectedData.Unprotect(
                    File.ReadAllBytes(_keyPath),
                    Entropy,
                    DataProtectionScope.CurrentUser);
                CryptographicOperations.ZeroMemory(key);
                return winner;
            }

            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
