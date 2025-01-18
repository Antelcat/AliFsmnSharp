namespace AliFsmnSharp;


/// <summary>
/// 逐字字幕，每个字都有自己的时间戳
/// </summary>
public record AccurateTextSpan(IReadOnlyList<TextSpan> parts)
    : TextSpan(string.Join(null, parts.Select(p => p.Text)), parts[0].Begin, parts[^1].End)
{
    public IReadOnlyList<TextSpan> Parts => parts;

    public (TextSpan, TextSpan) Split(TimeSpan time)
    {
        if (time <= Begin || time >= End)
        {
            throw new ArgumentOutOfRangeException(nameof(time), time, "Time is out of range");
        }

        var left  = new List<TextSpan>();
        var right = new List<TextSpan>();
        foreach (var part in Parts)
        {
            if (part.End <= time)
            {
                left.Add(part);
            }
            else if (part.Begin >= time)
            {
                right.Add(part);
            }
            else
            {
                left.Add(new TextSpan(part.Text, part.Begin, time));
                right.Add(new TextSpan(part.Text, time, part.End));
            }
        }

        return (
            left.Count  == 1 ? left[0] : new AccurateTextSpan(left),
            right.Count == 1 ? right[0] : new AccurateTextSpan(right)
        );
    }

    public override string ToString() => base.ToString();
}