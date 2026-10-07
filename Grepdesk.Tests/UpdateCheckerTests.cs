using System.Net;
using System.Text;
using Grepdesk.UI.Updates;

namespace Grepdesk.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.0.1", "1.0.0", true)]
    [InlineData("1.1", "1.0.9", true)]
    [InlineData("v2.0.0-beta", "1.9.0", true)]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("v1.0", "1.0.0", false)]
    [InlineData("v0.9.0", "1.0.0", false)]
    [InlineData("nightly", "1.0.0", false)]
    public void Versions_compare_by_number(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(candidate, current));

    [Fact]
    public async Task A_newer_release_is_reported_with_its_page()
    {
        var checker = Checker(HttpStatusCode.OK, """{"tag_name":"v1.2.0","html_url":"https://github.com/x/y/releases/tag/v1.2.0"}""");

        var result = await checker.CheckAsync("1.0.0", default);

        Assert.Equal(new UpdateResult(UpdateStatus.Available, "1.2.0", "https://github.com/x/y/releases/tag/v1.2.0"), result);
    }

    [Fact]
    public async Task The_same_version_is_up_to_date()
    {
        var checker = Checker(HttpStatusCode.OK, """{"tag_name":"v1.0.0","html_url":"https://example.com"}""");

        Assert.Equal(UpdateStatus.UpToDate, (await checker.CheckAsync("1.0.0", default)).Status);
    }

    [Fact]
    public async Task No_release_yet_is_up_to_date()
    {
        var checker = Checker(HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        Assert.Equal(UpdateStatus.UpToDate, (await checker.CheckAsync("1.0.0", default)).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""")]
    [InlineData(HttpStatusCode.OK, "<html>not json</html>")]
    [InlineData(HttpStatusCode.OK, """{"name":"no tag"}""")]
    public async Task Errors_and_odd_answers_fail_quietly(HttpStatusCode status, string body)
    {
        var checker = Checker(status, body);

        Assert.Equal(UpdateStatus.Failed, (await checker.CheckAsync("1.0.0", default)).Status);
    }

    [Fact]
    public async Task Being_offline_fails_quietly()
    {
        var checker = new UpdateChecker(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline"))));

        Assert.Equal(UpdateStatus.Failed, (await checker.CheckAsync("1.0.0", default)).Status);
    }

    [Fact]
    public async Task Requests_identify_the_app()
    {
        HttpRequestMessage? sent = null;
        var checker = new UpdateChecker(new HttpClient(new FakeHandler(request =>
        {
            sent = request;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })));

        await checker.CheckAsync("1.0.0", default);

        Assert.Equal("Grepdesk/1.0.0", sent!.Headers.UserAgent.ToString());
    }

    private static UpdateChecker Checker(HttpStatusCode status, string body) =>
        new(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        })));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
