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

// Two headers naming one path would write the second over the first, leaving a published sample
// uncompiled while the harness still reported a full extraction. Case-insensitively, because the
// file system this writes to on Windows is, so a difference in case is still one file.
var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
for (var index = 0; index < multiFile.Count; index++)
{
    var (file, code) = multiFile[index];

    // The header names a path inside the sample's own project (Users/...), whose root is Users/Generated.
    const string prefix = "Users/";
    if (!file.StartsWith(prefix, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"docs/quickstart.md: sample file '{file}' is outside the {prefix} project.");
    }

    if (targets.TryGetValue(file, out var earlier))
    {
        throw new InvalidOperationException(
            $"docs/quickstart.md: '// File: {file}' heads both sample {earlier} and sample {index + 1} "
                + "(counting the '// File:' samples in document order); writing the second would leave the first uncompiled. "
                + "Give each sample its own path."
        );
    }
    targets.Add(file, index + 1);

    Write($"Users/Generated/{file[prefix.Length..]}", code);
}

void Write(string relativePath, string code)
{
    var path = Path.Combine(samplesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, code + "\n", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    Console.WriteLine($"DocSamples: extracted {relativePath}");
}
