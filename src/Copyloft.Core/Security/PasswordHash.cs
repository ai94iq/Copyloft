using System.Security.Cryptography;

namespace Copyloft.Core.Security;

// Stores a salted PBKDF2 hash as "iterations.salt.hash"; the plain password is never kept.
public static class PasswordHash
{
    private const int Iterations = 600_000;
    private const int MaxIterations = 10 * Iterations;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        // A hash this code never wrote (an edited or corrupt value) is a wrong password, never a
        // throw or a minutes-long derivation.
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations) || iterations < 1 || iterations > MaxIterations) return false;

        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            // An empty hash part would compare equal to an empty derivation: any password.
            if (expected.Length == 0) return false;

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException)
        {
            return false;
        }
    }
}
