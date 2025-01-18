namespace AliFsmnSharp;

public record TextSpan(string Text, TimeSpan Begin, TimeSpan End)
{
    public override string ToString()
    {
        return $"""
               {Begin} --> {End}
               {Text}
               """;
    }
}
