using AliFsmnSharp.TestConsole;
Console.WriteLine("Please enter a file path:");
Console.Write("> ");
var filePath  = Console.ReadLine();
while (filePath is null || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
{
    Console.WriteLine("Please enter a valid file path:");
    Console.Write("> ");
    filePath = Console.ReadLine();
}
var generator = new SubtitleGenerator();
generator.Subtitles.CollectionChanged += (_, e) => {
    Console.WriteLine(e.NewItems![0]!.ToString());
};
await generator.Start(filePath, true, () => TimeSpan.Zero, CancellationToken.None);
