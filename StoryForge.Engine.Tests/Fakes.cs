using System.Net;
using System.Text;
using StoryForge.Engine.Providers;

namespace StoryForge.Engine.Tests;

/// <summary>Answers per executable name; anything unknown is "not found".</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, Func<string, ProcessRunResult>> _answers = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Executable, string Arguments)> Calls { get; } = [];

    public FakeProcessRunner Answer(string executable, Func<string, ProcessRunResult> answer)
    {
        _answers[executable] = answer;
        return this;
    }

    public Task<ProcessRunResult> RunAsync(string executable, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add((executable, arguments));
        return _answers.TryGetValue(executable, out var answer)
            ? Task.FromResult(answer(arguments))
            : throw new ExecutableNotFoundException(executable);
    }
}

/// <summary>Answers HTTP requests by URL; anything unknown is refused like a closed port.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _answers = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHttpHandler Answer(string url, HttpStatusCode status, string body = "") =>
        Answer(url, _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    public FakeHttpHandler Answer(string url, Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        _answers[url] = answer;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return _answers.TryGetValue(request.RequestUri!.ToString(), out var answer)
            ? Task.FromResult(answer(request))
            : throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
    }
}
