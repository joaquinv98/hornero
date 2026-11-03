using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BillingOss.Updates.Tests;

/// <summary>La parte de la API v1 de Forgejo que usa el vigía, en memoria: etiquetas, issues, comentarios y PR.</summary>
internal sealed class FakeForgejo
{
    private readonly Lock gate = new();

    public List<(long Id, string Name)> Labels { get; } = [];

    public List<FakeIssue> Issues { get; } = [];

    public List<(long Number, string Title)> Pulls { get; } = [];

    /// <summary>El encabezado Authorization de cada pedido.</summary>
    public List<string?> Authorized { get; } = [];

    /// <summary>La hora que anota como created_at.</summary>
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public IEnumerable<string> LabelNames(FakeIssue issue) => issue.LabelIds.Select(id => Labels.Single(l => l.Id == id).Name);

    public void Map(WebApplication app)
    {
        const string Repo = "/api/v1/repos/{owner}/{repo}";
        app.Use((http, next) =>
        {
            lock (gate)
            {
                Authorized.Add(http.Request.Headers.Authorization.ToString() is { Length: > 0 } auth ? auth : null);
            }

            return next(http);
        });
        app.MapGet(Repo + "/labels", (HttpContext http) => Page(http, Labels.Select(l => new { id = l.Id, name = l.Name })));
        app.MapPost(Repo + "/labels", async (HttpContext http) =>
        {
            var body = await Body(http);
            lock (gate)
            {
                var label = (Id: Labels.Count + 100L, Name: body["name"]!.GetValue<string>());
                Labels.Add(label);
                return Results.Json(new { id = label.Id, name = label.Name }, statusCode: 201);
            }
        });
        app.MapGet(Repo + "/issues", (HttpContext http) =>
        {
            var state = http.Request.Query["state"].ToString();
            var label = http.Request.Query["labels"].ToString();
            return Page(http, Issues.Where(i => i.State == state && LabelNames(i).Contains(label)).Select(Json));
        });
        app.MapPost(Repo + "/issues", async (HttpContext http) =>
        {
            var body = await Body(http);
            lock (gate)
            {
                var issue = new FakeIssue
                {
                    Number = Issues.Count + 1,
                    Title = body["title"]!.GetValue<string>(),
                    Body = body["body"]!.GetValue<string>(),
                    CreatedAt = Now,
                    LabelIds = [.. body["labels"]!.AsArray().Select(l => l!.GetValue<long>())],
                };
                Issues.Add(issue);
                return Results.Json(Json(issue), statusCode: 201);
            }
        });
        app.MapMethods(Repo + "/issues/{number:long}", ["PATCH"], async (long number, HttpContext http) =>
        {
            var body = await Body(http);
            var issue = Issues.Single(i => i.Number == number);
            issue.Body = body["body"]?.GetValue<string>() ?? issue.Body;
            issue.State = body["state"]?.GetValue<string>() ?? issue.State;
            return Results.Json(Json(issue), statusCode: 201);
        });
        app.MapPost(Repo + "/issues/{number:long}/comments", async (long number, HttpContext http) =>
        {
            Issues.Single(i => i.Number == number).Comments.Add((await Body(http))["body"]!.GetValue<string>());
            return Results.Json(new { id = 1 }, statusCode: 201);
        });
        app.MapPost(Repo + "/issues/{number:long}/labels", async (long number, HttpContext http) =>
        {
            var issue = Issues.Single(i => i.Number == number);
            issue.LabelIds = [.. issue.LabelIds.Union((await Body(http))["labels"]!.AsArray().Select(l => l!.GetValue<long>()))];
            return Results.Json(LabelNames(issue).Select(n => new { name = n }));
        });
        app.MapPost(Repo + "/issues/{number:long}/pin", (long number) =>
        {
            Issues.Single(i => i.Number == number).Pinned = true;
            return Results.NoContent();
        });
        app.MapGet(Repo + "/pulls", (HttpContext http) => Page(http, Pulls.Select(p => new { number = p.Number, title = p.Title })));
    }

    private object Json(FakeIssue i) => new
    {
        number = i.Number,
        title = i.Title,
        body = i.Body,
        state = i.State,
        created_at = i.CreatedAt,
        labels = LabelNames(i).Select(n => new { id = Labels.Single(l => l.Name == n).Id, name = n }),
    };

    private static IResult Page<T>(HttpContext http, IEnumerable<T> items)
    {
        var limit = int.Parse(http.Request.Query["limit"].ToString() is { Length: > 0 } l ? l : "30", System.Globalization.CultureInfo.InvariantCulture);
        var page = int.Parse(http.Request.Query["page"].ToString() is { Length: > 0 } p ? p : "1", System.Globalization.CultureInfo.InvariantCulture);
        return Results.Json(items.Skip((page - 1) * limit).Take(limit).ToList());
    }

    private static async Task<JsonNode> Body(HttpContext http) => (await JsonNode.ParseAsync(http.Request.Body))!;
}

internal sealed class FakeIssue
{
    public long Number { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string State { get; set; } = "open";

    public DateTimeOffset CreatedAt { get; init; }

    public long[] LabelIds { get; set; } = [];

    public List<string> Comments { get; } = [];

    public bool Pinned { get; set; }
}
