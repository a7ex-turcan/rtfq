using System.Security.Cryptography;
using System.Text;
using Rtfq.Server.Configuration;
using Rtfq.Server.Policy;

namespace Rtfq.Server.Auth;

/// <summary>
/// Bearer-token authentication with constant-time comparison.
///
/// Every configured token is compared on every attempt, and the result is
/// accumulated rather than returned early, so neither the number of tokens nor
/// the position of a match is observable in the response time.
/// </summary>
public sealed class TokenAuthenticator
{
    readonly (string Id, byte[] Secret, IReadOnlyDictionary<string, Contracts.AccessLevel> Grants)[] _tokens;

    public TokenAuthenticator(RtfqConfig config)
    {
        _tokens = [.. config.Server.Auth.Tokens.Select(t => (t.Id, Encoding.UTF8.GetBytes(t.Secret), t.Grants))];
    }

    /// <summary>Returns the caller, or null if the presented secret matches no configured token.</summary>
    public Caller? Authenticate(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return null;

        var candidate = Encoding.UTF8.GetBytes(presented);
        Caller? matched = null;

        foreach (var (id, secret, grants) in _tokens)
        {
            // FixedTimeEquals is length-sensitive by contract, so guard the length
            // check separately rather than letting it short-circuit the comparison.
            var equal = secret.Length == candidate.Length &&
                        CryptographicOperations.FixedTimeEquals(secret, candidate);

            if (equal) matched = new Caller(id, grants);
        }

        return matched;
    }

    /// <summary>Extracts the secret from an <c>Authorization: Bearer &lt;token&gt;</c> header.</summary>
    public static string? ExtractBearer(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return null;

        const string prefix = "Bearer ";
        return authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader[prefix.Length..].Trim()
            : null;
    }
}
