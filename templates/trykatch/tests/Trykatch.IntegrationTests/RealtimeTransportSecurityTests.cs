using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using static Trykatch.IntegrationTests.AccountSecurityStepUpTests;

namespace Trykatch.IntegrationTests;

[TestClass, TestCategory("Integration")]
public sealed class RealtimeTransportSecurityTests
{
    private const string NegotiatePath = "/api/v1/realtime/hub/negotiate?negotiateVersion=1";

    [TestMethod]
    public async Task NegotiateRequiresAnAuthenticatedCookieTrustedOriginAndAntiforgery()
    {
        await using SecurityHost host = await StartLocalRealtimeAsync();
        using HttpClient anonymous = host.CreateClient();
        using (HttpResponseMessage response = await NegotiateAsync(anonymous, ["https://localhost"]))
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await response.Content.ReadAsStringAsync());

        using HttpClient browser = host.CreateClient();
        await AccountSessionLifecycleTests.SignInAsync(host, browser);

        using (HttpResponseMessage foreign = await NegotiateAsync(browser, ["https://attacker.example"]))
            foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await foreign.Content.ReadAsStringAsync());
        using (HttpResponseMessage missing = await NegotiateAsync(browser, []))
            missing.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await missing.Content.ReadAsStringAsync());
        using (HttpResponseMessage multiple = await NegotiateAsync(browser, ["https://localhost", "https://attacker.example"]))
            multiple.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await multiple.Content.ReadAsStringAsync());

        using (HttpResponseMessage withoutCsrf = await NegotiateAsync(browser, ["https://localhost"]))
            withoutCsrf.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await withoutCsrf.Content.ReadAsStringAsync());

        string csrf = await GetAntiforgeryTokenAsync(browser);
        using HttpResponseMessage negotiated = await NegotiateAsync(browser, ["https://localhost"], csrf);
        negotiated.StatusCode.ShouldBe(HttpStatusCode.OK, await negotiated.Content.ReadAsStringAsync());
        JsonElement payload = await negotiated.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement.ArrayEnumerator transports = payload.GetProperty("availableTransports").EnumerateArray();
        transports.Count().ShouldBe(1);
        transports.Single().GetProperty("transport").GetString().ShouldBe("WebSockets");
    }

    [TestMethod]
    public async Task NegotiateRejectsAccessTokenQueryEvenWithCookieOriginAndAntiforgery()
    {
        await using SecurityHost host = await StartLocalRealtimeAsync();
        using HttpClient browser = host.CreateClient();
        await AccountSessionLifecycleTests.SignInAsync(host, browser);
        string csrf = await GetAntiforgeryTokenAsync(browser);

        using HttpResponseMessage response = await NegotiateAsync(browser,
            ["https://localhost"], csrf, "/api/v1/realtime/hub/negotiate?negotiateVersion=1&access_token=must-not-be-used");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task ConfiguredPublicOriginIsTheExactAllowedExternalOrigin()
    {
        await using SecurityHost host = await StartLocalRealtimeAsync("https://realtime.trykatch.test");
        using HttpClient browser = host.CreateClient();
        await AccountSessionLifecycleTests.SignInAsync(host, browser);
        string csrf = await GetAntiforgeryTokenAsync(browser);

        using (HttpResponseMessage external = await NegotiateAsync(browser, ["https://realtime.trykatch.test"], csrf))
            external.StatusCode.ShouldBe(HttpStatusCode.OK, await external.Content.ReadAsStringAsync());
        using (HttpResponseMessage requestOrigin = await NegotiateAsync(browser, ["https://localhost"], csrf))
            requestOrigin.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await requestOrigin.Content.ReadAsStringAsync());
    }

    private static async Task<SecurityHost> StartLocalRealtimeAsync(string? publicUrl = null)
    {
        SecurityHost host = await SecurityHost.StartAsync();
        Dictionary<string, string?> settings = new() { ["Realtime:Mode"] = "LocalOnly" };
        if (publicUrl is not null) settings["Application:PublicUrl"] = publicUrl;
        await host.RestartAsync(settings, "Development");
        return host;
    }

    private static async Task<HttpResponseMessage> NegotiateAsync(
        HttpClient client,
        string[] origins,
        string? csrf = null,
        string? path = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path ?? NegotiatePath);
        if (origins.Length > 0)
            request.Headers.TryAddWithoutValidation("Origin", origins).ShouldBeTrue();
        if (csrf is not null)
            request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        JsonElement antiforgery = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery");
        return antiforgery.GetProperty("token").GetString()!;
    }
}
