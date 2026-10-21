using System.Net.Http.Headers;
using System.Text;

namespace ndx;

public sealed record SourceCredential(string Source, string Username, string Password);

/// <summary>
/// Adds basic authentication from nuget.config packageSourceCredentials.
/// </summary>
public sealed class SourceCredentialsHandler(IEnumerable<SourceCredential> credentials) : DelegatingHandler
{
    readonly IReadOnlyList<(Uri source, AuthenticationHeaderValue header)> credentials = BuildHeaders(credentials);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null && request.RequestUri is { } target)
        {
            foreach (var (source, header) in credentials)
            {
                if (Uri.Compare(source, target, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    request.Headers.Authorization = header;
                    break;
                }
            }
        }

        return base.SendAsync(request, cancellationToken);
    }

    static List<(Uri Source, AuthenticationHeaderValue Header)> BuildHeaders(IEnumerable<SourceCredential> credentials)
    {
        var headers = new List<(Uri Source, AuthenticationHeaderValue Header)>();
        foreach (var credential in credentials)
        {
            if (!Uri.TryCreate(credential.Source, UriKind.Absolute, out var source) ||
                (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Password}"));
            headers.Add((source, new AuthenticationHeaderValue("Basic", token)));
        }

        return headers;
    }
}
