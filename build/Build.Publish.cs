using System;
using System.Net.Http;

using Fallout.Common;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Utilities.Collections;
using Fallout.Common.Utilities.Net;

using Serilog;

using static Fallout.Common.Tools.DotNet.DotNetTasks;

/// <summary>
/// The NuGet distribution channel (D22): <c>Pack</c> builds the framework-dependent .NET tool
/// package and <c>Publish</c> pushes it to nuget.org with a short-lived API key minted from the
/// workflow's GitHub OIDC token (trusted publishing - no stored NuGet API key anywhere).
/// </summary>
/// <remarks>
/// This is the channel the Claude Code plugin launches (D22): both of its server entries run
/// <c>dnx claude-roslyn-lsp@{version}</c>, so the package has to exist before a plugin release is
/// worth anything. The Native AOT binaries on GitHub Releases are what every file-based client
/// points at instead. There is no preview feed - the only push this repository ever makes is a
/// tagged release to nuget.org, which is why <c>Publish</c> is a single-source target gated on
/// <see cref="Build.IsTaggedBuild"/> rather than a two-way switch between a release feed and a
/// preview one.
/// </remarks>
partial class Build
{
    const string NuGetSource = "https://api.nuget.org/v3/index.json";

    /// <summary>The OIDC audience nuget.org expects. Not <c>nuget</c>, whatever the design doc says.</summary>
    const string NuGetAudience = "https://www.nuget.org";

    const string NuGetTokenServiceUrl = "https://www.nuget.org/api/v2/token";

    /// <summary>
    /// Any non-empty User-Agent will do, but there must be one - see
    /// <see cref="GetTrustedPublishingApiKey"/>.
    /// </summary>
    const string NuGetUserAgent = "claude-roslyn-lsp-build/1.0";

    /// <summary>The workflow file the nuget.org trusted publishing policy is scoped to.</summary>
    /// <remarks>
    /// Only used in the error message, but it is the field people get wrong, so it is worth naming
    /// the actual value rather than describing it.
    /// </remarks>
    const string PublishWorkflowFile = "publish.yml";

    [Parameter("nuget.org profile name of the account that owns the trusted publishing policy " +
               "(env: NUGET_USER). Public information, not a secret.")]
    readonly string NuGetUser;

    AbsolutePath PackagesDirectory => ArtifactsDirectory / "packages";

    Target Pack => _ => _
        .Description("Packs the server as a .NET tool / MCP server NuGet package into artifacts/packages")
        .Produces(PackagesDirectory / "*.nupkg")
        .Executes(() =>
        {
            // Independent of Compile for the same reason PublishAot is: this is a Release build of
            // one project, and `dotnet pack` of a tool package runs its own publish anyway.
            PackagesDirectory.CreateOrCleanDirectory();

            DotNetPack(_ => _
                .SetProject(ServerProject)
                .SetConfiguration(Configuration.Release)
                .SetOutputDirectory(PackagesDirectory)
                .SetProperty("Version", Version));

            var packages = PackagesDirectory.GlobFiles("*.nupkg");
            Assert.NotEmpty(packages, $"dotnet pack produced no .nupkg in '{PackagesDirectory}'");

            foreach (var package in packages)
            {
                Log.Information("Packed {Package}", package.Name);
            }

            ReportSummary(_ => _
                .AddPair("Version", Version)
                .AddPair("Packages", packages.Count.ToString()));
        });

    Target Publish => _ => _
        .Description("Pushes artifacts/packages to nuget.org using a trusted-publishing (OIDC) key")
        .DependsOn(Pack)
        .OnlyWhenDynamic(() => ShouldPublish())
        .Executes(() =>
        {
            AssertPublishContext();

            var packages = PackagesDirectory.GlobFiles("*.nupkg");
            Assert.NotEmpty(packages, $"Nothing to publish: no .nupkg in '{PackagesDirectory}'");

            // Minted here rather than in an earlier target or job: the key lives 15-60 minutes and
            // one OIDC token mints exactly one key.
            var apiKey = GetTrustedPublishingApiKey();

            DotNetNuGetPush(_ => _
                    .SetSource(NuGetSource)
                    .SetApiKey(apiKey)
                    .EnableSkipDuplicate()
                    .CombineWith(packages, (_, v) => _.SetTargetPath(v)),
                degreeOfParallelism: 1,
                completeOnFailure: true);

            ReportSummary(_ => _
                .AddPair("Pushed", packages.Count.ToString())
                .AddPair("Version", Version));
        });

    /// <summary>
    /// The tag gate. There is exactly one thing a <c>Publish</c> run can legitimately be - a v* tag
    /// build pushing to nuget.org - so anything else is skipped rather than failed, which keeps
    /// <c>Compile Test Pack Publish</c> a safe line to run anywhere. Skipping is never silent.
    /// </summary>
    bool ShouldPublish()
    {
        if (IsTaggedBuild)
        {
            return true;
        }

        Log.Warning(
            "Skipping Publish: this build is not a v* tag build, and nothing is pushed to nuget.org from " +
            "anything else (there is no preview feed). Run `Pack` to build and inspect the package locally; " +
            "push tag v{Version} - the version CHANGELOG.md declares - to publish it.", Version);

        return false;
    }

    /// <summary>
    /// Refuses to run anywhere the OIDC exchange cannot possibly succeed, with the reason spelled
    /// out. Same tag-versus-CHANGELOG rule as <c>CreateGitHubRelease</c>: CHANGELOG.md is the
    /// version authority, so the tag has to agree with it or the package version and the release
    /// name would tell different stories.
    /// </summary>
    void AssertPublishContext()
    {
        Assert.True(GitHubActions.Instance != null,
            "Publish only runs on GitHub Actions: the nuget.org API key is minted from the workflow's " +
            "OIDC token, which exists nowhere else. Run `.\\build.ps1 Pack` locally to inspect the package, " +
            "and push a v* tag to publish.");

        Assert.NotNullOrWhiteSpace(NuGetUser,
            "The nuget.org profile name of the trusted publishing policy's creator is required. Set it as " +
            "NUGET_USER (env:) on the publish job, or pass --nuget-user. It is public information, " +
            "not a secret.");

        var expected = $"v{Version}";
        var actual = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");

        Assert.True(actual == expected,
            $"Refusing to publish to nuget.org: the workflow ran for ref '{actual}' but CHANGELOG.md says the " +
            $"version is {Version} (tag '{expected}'). Tag the commit that carries the matching CHANGELOG entry.");
    }

    /// <summary>
    /// Exchanges the job's GitHub OIDC token for a short-lived nuget.org API key - what
    /// <c>NuGet/login@v1</c> does, without taking a dependency on a marketplace action.
    /// </summary>
    string GetTrustedPublishingApiKey()
    {
        const string MissingOidc =
            "GitHub OIDC is unavailable - the publish job needs 'permissions: id-token: write'";

        var requestUrl = Assert.NotNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL"), MissingOidc);
        var requestToken = Assert.NotNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN"), MissingOidc);

        using var client = new HttpClient();

        // REQUIRED. nuget.org's token endpoint sits behind Azure Front Door, which answers
        // HTTP 400 "A User-Agent header is required." to a request that carries none - and a bare
        // HttpClient sends none. The check runs before the token or the policy is looked at, so the
        // symptom of forgetting this is a 400 rather than the 401 a misconfiguration would give.
        // The JS http client behind NuGet/login always sends one, which is why only in-build .NET
        // callers ever hit it.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(NuGetUserAgent);

        var idToken = client
            .CreateRequest(HttpMethod.Get, $"{requestUrl}&audience={Uri.EscapeDataString(NuGetAudience)}")
            .WithBearerAuthentication(requestToken)
            .GetResponse()
            .AssertSuccessfulStatusCode()
            .GetBodyAsJsonObject().GetAwaiter().GetResult()["value"].GetValue<string>();

        var body = client
            .CreateRequest(HttpMethod.Post, NuGetTokenServiceUrl)
            .WithBearerAuthentication(idToken)
            .WithJsonContent(new { username = NuGetUser, tokenType = "ApiKey" })
            .GetResponse()
            // Surface the response body: nuget.org answers { "error": "..." } and it usually names
            // the cause outright.
            .AssertResponse(x => x.IsSuccessStatusCode
                ? null
                : $"nuget.org token exchange failed ({(int) x.StatusCode}): " +
                  $"{x.Content.ReadAsStringAsync().GetAwaiter().GetResult()}. A 400 means the request shape is " +
                  "wrong (checked before any policy lookup); a 401 means no trusted publishing policy matched " +
                  $"repository lahma/claude-roslyn-lsp, workflow file {PublishWorkflowFile}, environment nuget and " +
                  $"creator '{NuGetUser}'.")
            .GetBodyAsJsonObject().GetAwaiter().GetResult();

        // The action reads 'apiKey'; the original design document says 'api_key'. Accept either.
        var apiKey = (body["apiKey"] ?? body["api_key"])
            .NotNull($"nuget.org returned no API key: {body.ToJsonString()}")
            .GetValue<string>();

        GitHubActions.Instance?.WriteCommand("add-mask", apiKey);
        Log.Information("Minted a short-lived nuget.org API key, expires {Expires}",
            body["expires"]?.GetValue<string>() ?? "(not reported)");

        return apiKey;
    }
}
