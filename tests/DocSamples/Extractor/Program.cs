using Acta.Tests.Docs;

// Materializes the documented samples as source files inside the sample projects run.ps1 compiles
// against the packed Acta packages. Writing is the only side effect; every selection rule lives in
// DocSampleExtraction, shared with the unit drift gates.
var samplesRoot = args.Length == 1 ? args[0] : throw new ArgumentException("usage: Extractor <tests/DocSamples path>");
var canonicalDocument = DocSampleExtraction.FirstRunDocuments[0];

// The docs must agree before anything is written: compiling one door's copy proves nothing about
// the others unless they are the same program. The unit gate says the same thing without packages.
var firstRun = DocSampleExtraction.FirstRunProgram(canonicalDocument);
foreach (var document in DocSampleExtraction.FirstRunDocuments)
{
    if (DocSampleExtraction.FirstRunProgram(document) != firstRun)
    {
        throw new InvalidOperationException(
            $"{document} publishes a different first-run program than {canonicalDocument}; the compile harness refuses to pick a winner."
        );
    }
}

Write("FirstRun/Generated/Program.cs", firstRun);
Write("Webhook/Generated/WebhookJobs.cs", DocSampleExtraction.BlockContaining("llms.txt", "class WebhookJobs"));

var multiFile = DocSampleExtraction.FileHeaderedBlocks("docs/quickstart.md");
if (multiFile.Count != 3)
{
    throw new InvalidOperationException($"docs/quickstart.md: expected 3 '// File:' sample files, found {multiFile.Count}.");
}

foreach (var (file, code) in multiFile)
{
    // The header names a path inside the sample's own project (Users/...), whose root is Users/Generated.
    const string prefix = "Users/";
    if (!file.StartsWith(prefix, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"docs/quickstart.md: sample file '{file}' is outside the {prefix} project.");
    }
    Write($"Users/Generated/{file[prefix.Length..]}", code);
}

void Write(string relativePath, string code)
{
    var path = Path.Combine(samplesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, code + "\n", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    Console.WriteLine($"DocSamples: extracted {relativePath}");
}
