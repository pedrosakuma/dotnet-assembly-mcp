using System.Net;
using System.Text;
using DotnetAssemblyMcp.Server.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Validates the static bearer-token middleware: opt-in, /health exempt, missing/wrong tokens
/// return 401, correct token passes through. Mirrors the contract enforced by
/// <c>dotnet-diagnostics-mcp</c>'s equivalent middleware.
/// </summary>
public sealed class BearerTokenMiddlewareTests
{
    private const string Token = "supersecrettoken1234567890";

    private static async Task<HttpContext> Invoke(string path, string? authHeader)
    {
        var hit = false;
        RequestDelegate next = ctx => { hit = true; ctx.Response.StatusCode = StatusCodes.Status200OK; return Task.CompletedTask; };
        var mw = new BearerTokenMiddleware(next, new BearerTokenOptions { Token = Token });

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        if (authHeader is not null) ctx.Request.Headers.Authorization = authHeader;

        await mw.InvokeAsync(ctx);
        ctx.Items["hit"] = hit;
        return ctx;
    }

    [Fact]
    public async Task Health_endpoint_is_exempt_even_without_token()
    {
        var ctx = await Invoke("/health", authHeader: null);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ((bool)ctx.Items["hit"]!).Should().BeTrue();
    }

    [Fact]
    public async Task Missing_authorization_header_returns_401()
    {
        var ctx = await Invoke("/mcp", authHeader: null);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        ctx.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer");
        ((bool)ctx.Items["hit"]!).Should().BeFalse();
    }

    [Fact]
    public async Task Wrong_scheme_returns_401()
    {
        var ctx = await Invoke("/mcp", authHeader: $"Basic {Token}");
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Wrong_token_returns_401()
    {
        var ctx = await Invoke("/mcp", authHeader: "Bearer wrong");
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Correct_token_passes_through()
    {
        var ctx = await Invoke("/mcp", authHeader: $"Bearer {Token}");
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ((bool)ctx.Items["hit"]!).Should().BeTrue();
    }

    [Fact]
    public async Task Bearer_scheme_match_is_case_insensitive()
    {
        var ctx = await Invoke("/mcp", authHeader: $"bearer {Token}");
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public void TryLoad_returns_null_when_no_env_var_set()
    {
        Environment.SetEnvironmentVariable(BearerTokenOptions.PrimaryEnvVar, null);
        Environment.SetEnvironmentVariable(BearerTokenOptions.FallbackEnvVar, null);
        var opts = BearerTokenOptions.TryLoad(NullLogger.Instance);
        opts.Should().BeNull();
    }

    [Fact]
    public void TryLoad_prefers_primary_env_var_over_fallback()
    {
        try
        {
            Environment.SetEnvironmentVariable(BearerTokenOptions.PrimaryEnvVar, "primary");
            Environment.SetEnvironmentVariable(BearerTokenOptions.FallbackEnvVar, "fallback");
            var opts = BearerTokenOptions.TryLoad(NullLogger.Instance);
            opts.Should().NotBeNull();
            opts!.Token.Should().Be("primary");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BearerTokenOptions.PrimaryEnvVar, null);
            Environment.SetEnvironmentVariable(BearerTokenOptions.FallbackEnvVar, null);
        }
    }

    [Fact]
    public void TryLoad_falls_back_to_shared_env_var()
    {
        try
        {
            Environment.SetEnvironmentVariable(BearerTokenOptions.PrimaryEnvVar, null);
            Environment.SetEnvironmentVariable(BearerTokenOptions.FallbackEnvVar, "shared");
            var opts = BearerTokenOptions.TryLoad(NullLogger.Instance);
            opts.Should().NotBeNull();
            opts!.Token.Should().Be("shared");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BearerTokenOptions.FallbackEnvVar, null);
        }
    }
}
