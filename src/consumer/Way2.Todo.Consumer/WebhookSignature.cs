using System.Security.Cryptography;
using System.Text;

namespace Way2.Todo.Consumer;

/// <summary>
/// Verifica <c>X-Way2-Signature: t=&lt;unix&gt;,v1=&lt;hex HMAC-SHA256(segredo, "t." + corpo)&gt;</c> sobre os
/// bytes CRUS do corpo (antes de qualquer desserialização), com janela de tempo nos dois sentidos.
/// </summary>
public static class WebhookSignature
{
    public const string Header = "X-Way2-Signature";

    public static bool IsValid(string? header, byte[] body, string secret, DateTimeOffset now, int toleranceSeconds)
    {
        if (string.IsNullOrEmpty(header))
            return false;

        long? timestamp = null;
        byte[]? provided = null;
        foreach (var part in header.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
                return false;
            if (pair[0] == "t" && long.TryParse(pair[1], out var t))
                timestamp = t;
            else if (pair[0] == "v1")
                provided = TryFromHex(pair[1]);
        }
        if (timestamp is null || provided is null)
            return false;
        if (Math.Abs(now.ToUnixTimeSeconds() - timestamp.Value) > toleranceSeconds)
            return false;

        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed, prefix.Length);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private static byte[]? TryFromHex(string value)
    {
        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
