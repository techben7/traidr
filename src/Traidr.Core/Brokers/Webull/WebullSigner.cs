using System.Security.Cryptography;
using System.Text;

namespace Traidr.Core.Brokers.Webull;

public static class WebullSigner
{
    public static WebullSignedHeaders CreateSignedHeaders(
        Uri baseUri,
        string path,
        IReadOnlyDictionary<string, string> queryParams,
        string? bodyJson,
        string appKey,
        string appSecret)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var host = baseUri.Host;

        var headersForSig = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-app-key"] = appKey,
            ["x-timestamp"] = timestamp,
            ["x-signature-version"] = "1.0",
            ["x-signature-algorithm"] = "HMAC-SHA1",
            ["x-signature-nonce"] = nonce,
        };

        var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in queryParams) all[kv.Key] = kv.Value;
        foreach (var kv in headersForSig) all[kv.Key] = kv.Value;

        var s1 = string.Join("&", all.Select(kv => $"{kv.Key}={kv.Value}"));

        var source = new StringBuilder();
        source.Append(path).Append('&').Append(s1);

        if (!string.IsNullOrEmpty(bodyJson))
            source.Append('&').Append(Md5UpperHex(bodyJson));

        var encoded = UpperEscape(source.ToString());

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(appSecret + "&"));
        var sigBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(encoded));
        var signature = Convert.ToBase64String(sigBytes);

        return new WebullSignedHeaders(appKey, timestamp, nonce, signature, host);
    }

    private static string Md5UpperHex(string input)
    {
        using var md5 = MD5.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = md5.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    private static string UpperEscape(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if ((b >= (byte)'a' && b <= (byte)'z') ||
                (b >= (byte)'A' && b <= (byte)'Z') ||
                (b >= (byte)'0' && b <= (byte)'9') ||
                b == (byte)'-' || b == (byte)'_' || b == (byte)'.')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2"));
            }
        }
        return sb.ToString();
    }
}

public sealed record WebullSignedHeaders(
    string AppKey,
    string Timestamp,
    string Nonce,
    string Signature,
    string Host);
