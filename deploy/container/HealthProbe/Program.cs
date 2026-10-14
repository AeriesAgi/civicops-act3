// The chiseled runtime has no shell or curl. Probe only a loopback readiness endpoint.
var target = new Uri(args.Length == 0 ? "http://127.0.0.1:8080/health/ready" : args[0]);
if (!target.IsLoopback || target.Scheme != Uri.UriSchemeHttp) return 2;
using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
try
{
    using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead);
    return response.IsSuccessStatusCode ? 0 : 1;
}
catch (HttpRequestException) { return 1; }
catch (TaskCanceledException) { return 1; }
