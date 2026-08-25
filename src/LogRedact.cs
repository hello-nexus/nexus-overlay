using System;
using System.Text;

namespace Nexus.Overlay;

/// <summary>
/// Masks credential-bearing parameters before a URL reaches the log file.
/// Panel / dashboard / overlay navigation URLs carry the service pair token in
/// <c>?token=</c>, and users routinely zip %ProgramData%\Nexus\logs into
/// support bundles, so the raw value must never be written to disk. Only the
/// value is masked, so the log still shows the path and which parameters were
/// present.
/// </summary>
internal static class LogRedact
{
    private const string Mask = "***";

    // Matched as a case-insensitive SUBSTRING of the parameter name, so
    // access_token / id_token / api_key / client_secret / authorization are
    // covered without listing every spelling. Over-masking a benign parameter
    // is harmless; missing a credential is not. The overlay's own URLs only
    // use "token" - the rest cover what an external navigation target
    // (OAuth redirect, signed link) can carry.
    private static readonly string[] SensitiveKeys =
        { "token", "key", "secret", "password", "auth", "code", "sig", "session" };

    /// <summary>
    /// Returns <paramref name="url"/> with the value of every sensitive
    /// parameter replaced by <c>***</c>. Input is treated as text, not parsed
    /// as a <see cref="Uri"/>: a relative or malformed navigation target must
    /// still be logged, just without its secrets. '?', '&amp;' and '#' are all
    /// treated as separators, so a token carried in a fragment (hash-routed
    /// SPA) is masked too.
    /// </summary>
    public static string Url(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url ?? string.Empty;

        var start = url.AsSpan().IndexOfAny('?', '#');
        if (start < 0) return url;

        var sb = new StringBuilder(url.Length);
        sb.Append(url, 0, start + 1);

        var i = start + 1;
        while (i <= url.Length)
        {
            var next = url.AsSpan(i).IndexOfAny('&', '?', '#');
            var end = next < 0 ? url.Length : i + next;
            AppendMasked(sb, url.AsSpan(i, end - i));
            if (next < 0) break;
            sb.Append(url[end]);
            i = end + 1;
        }

        return sb.ToString();
    }

    private static void AppendMasked(StringBuilder sb, ReadOnlySpan<char> segment)
    {
        // A double-encoded target can spell the separator as %3D, which would
        // otherwise leave the whole "key%3Dvalue" segment intact.
        var eq = segment.IndexOf('=');
        var split = eq >= 0 ? eq : segment.Length;
        var sepLength = eq >= 0 ? 1 : 0;
        var encoded = segment[..split].IndexOf("%3D", StringComparison.OrdinalIgnoreCase);
        if (encoded >= 0)
        {
            split = encoded;
            sepLength = 3;
        }

        if (sepLength > 0 && IsSensitive(segment[..split]))
        {
            sb.Append(segment[..(split + sepLength)]).Append(Mask);
            return;
        }
        sb.Append(segment);
    }

    private static bool IsSensitive(ReadOnlySpan<char> name)
    {
        foreach (var key in SensitiveKeys)
        {
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
