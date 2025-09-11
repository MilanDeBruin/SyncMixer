namespace SyncMixerApi;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

public static class MixerClientHelpers
{
    private static readonly Regex Base62 = new(@"[A-Za-z0-9]{22}", RegexOptions.Compiled);

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

    public static string? ToSpotifyUri(Track t)
    {
        // 1) Als er al een geldige spotify: URI staat, gebruik die.
        if (!string.IsNullOrWhiteSpace(t.Uri) &&
            t.Uri.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
            return t.Uri;

        // 2) Anders bouw ‘m op basis van type + id
        var id = ExtractId(t.Id ?? t.Uri);
        if (string.IsNullOrWhiteSpace(id)) return null;

        return $"spotify:track:{id}";
    }

    private static string ExtractId(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        // Als het al exact een 22-char id is
        if (s.Length == 22 && Base62.IsMatch(s)) return s;

        // Zoek 22-char id ergens in de string (dekt URL/URI)
        var m = Base62.Match(s);
        return m.Success ? m.Value : "";
    }
}
