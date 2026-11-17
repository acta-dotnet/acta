# How to fan out to child jobs with Map or WaitChildren

Two ways to fan out: `EnqueueChildAsync` with `WaitChildrenAsync` for a few named children, and `MapAsync` for one child per item of a list.

- Section: Split work across child jobs, step 4
- Program: `learn/200-durable-execution/215-map-wait-children` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/215-map-wait-children
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Enqueue a few named children and wait for them together

Independent analyzes, none needs another's result: enqueue each, and they run at once.

The lines that matter:

```csharp
var exif = await context.EnqueueChildAsync("exif", new ExtractExif(photo.AssetId, photo.SourceUrl), ct);
var blurhash = await context.EnqueueChildAsync("blurhash", new ComputeBlurhash(photo.AssetId, photo.SourceUrl), ct);
var moderation = await context.EnqueueChildAsync("moderation", new ScanModeration(photo.AssetId, photo.SourceUrl), ct);
var analyze = await context.WaitChildrenAsync([exif, blurhash, moderation], ct);
```

In `map-wait-children.cs` (excerpt):

```csharp
var exif = await context.EnqueueChildAsync("exif", new ExtractExif(photo.AssetId, photo.SourceUrl), ct);
var blurhash = await context.EnqueueChildAsync("blurhash", new ComputeBlurhash(photo.AssetId, photo.SourceUrl), ct);
var moderation = await context.EnqueueChildAsync("moderation", new ScanModeration(photo.AssetId, photo.SourceUrl), ct);
var analyze = await context.WaitChildrenAsync([exif, blurhash, moderation], ct);

analyze.ThrowIfAnyFailed();

int[] widths = [256, 512, 1024, 2048];
```

## 2. Map a list onto children, one per item

Map: one resize per width, keyed by width so replay dedupes onto the same children.

The lines that matter:

```csharp
int[] widths = [256, 512, 1024, 2048];
var resized = await context.MapAsync(
    "resize",
    widths,
    itemKey: width => width,
    child: width => new Resize(photo.AssetId, photo.SourceUrl, width),
    ct
);
```

In `map-wait-children.cs` (excerpt):

```csharp
var blurhash = await context.EnqueueChildAsync("blurhash", new ComputeBlurhash(photo.AssetId, photo.SourceUrl), ct);
var moderation = await context.EnqueueChildAsync("moderation", new ScanModeration(photo.AssetId, photo.SourceUrl), ct);
var analyze = await context.WaitChildrenAsync([exif, blurhash, moderation], ct);

analyze.ThrowIfAnyFailed();

int[] widths = [256, 512, 1024, 2048];
var resized = await context.MapAsync(
    "resize",
    widths,
    itemKey: width => width,
    child: width => new Resize(photo.AssetId, photo.SourceUrl, width),
    ct
);

var renditionsReady = resized.Items.Count(i => i.Outcome.Succeeded);

// Two independent side effects, enqueued and waited for the same way.
var cdn = await context.EnqueueChildAsync("publish-cdn", new PublishCdn(photo.AssetId), ct);
var index = await context.EnqueueChildAsync("update-index", new UpdateIndex(photo.AssetId), ct);
```

## 3. Count what landed instead of failing the parent

Resizes are best effort: report how many landed rather than failing the photo.

The lines that matter:

```csharp
var renditionsReady = resized.Items.Count(i => i.Outcome.Succeeded);
```

In `map-wait-children.cs` (excerpt):

```csharp
var renditionsReady = resized.Items.Count(i => i.Outcome.Succeeded);

// Two independent side effects, enqueued and waited for the same way.
var cdn = await context.EnqueueChildAsync("publish-cdn", new PublishCdn(photo.AssetId), ct);
var index = await context.EnqueueChildAsync("update-index", new UpdateIndex(photo.AssetId), ct);
var published = await context.WaitChildrenAsync([cdn, index], ct);
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/215-map-wait-children
```

Its output must contain:

```text
photo asset-42: 4 renditions ready, published=True
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A child's name on the job reads is its `DeduplicationKey`: the call's name for `EnqueueChildAsync`, `{map}/{key}` for a map's child.
- A group waits for every child. It never fails fast and never cancels siblings.
- The parent wakes only when the child it is waiting on lands, so it can run several times before the group is done. Keep the work before the wait in a step.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to find the child job that handles one item](/learn/222-find-child-job.md)

## More

- [The whole program](/learn/215-map-wait-children.program.md): every file as compiled, 135 lines.
