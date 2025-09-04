namespace SyncMixerApi;

using System.Security.Cryptography;
using System.Text;

public class MixerClientHelpers
{
    // ==== Helpers ====
    public static (string verifier, string challenge) GeneratePkceCodes()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        using var sha = SHA256.Create();
        var challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string Base64Url(byte[] input) =>
        Convert.ToBase64String(input).Replace("+", "-").Replace("/", "_").Replace("=", "");

    public static string EnsureTrailingSlash(string uri) =>
        uri.EndsWith("/") ? uri : uri + "/";
}
