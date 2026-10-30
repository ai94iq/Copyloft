using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Copyloft.Desktop.Platform.Windows;

// The SQLCipher key for the history database, protected with Windows DPAPI for the current user.
// Deleting the file makes the existing history unreadable, so it is backed up with the database.
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyStore : IKeyStore
{
    public string LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var blob = File.ReadAllBytes(path);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
        }

        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var protectedBlob = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
        // Written aside and moved into place, so a crash mid-write cannot leave a half blob.
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, protectedBlob);
        File.Move(temporary, path);
        return key;
    }
}
