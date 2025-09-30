using System.Diagnostics;
using BicepRpc;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.WriteLine("Usage: BicepRpc <path-to-folder>");
    return;
}

var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
await using var bicepClient = await BicepClient.Initialize($"{homeDir}/.azure/bin/bicep", cts.Token);

var bicepFiles = Directory.GetFiles(args[0], "*.bicep", SearchOption.AllDirectories);
var stopwatch = Stopwatch.StartNew();

foreach (var fileChunk in bicepFiles.Chunk(Environment.ProcessorCount))
{
    await Task.WhenAll(fileChunk.Select(file => Task.Run(async () =>
    {
        var result = await bicepClient.Compile(new(file), cts.Token);
        foreach (var diag in result.Diagnostics)
        {
            Console.WriteLine($"{file}({diag.Range.Start.Line},{diag.Range.Start.Char}): {diag.Level} {diag.Code}: {diag.Message}");
        }
    })));
}

Console.WriteLine($"Compiled {bicepFiles.Length} files in {stopwatch.ElapsedMilliseconds}ms");