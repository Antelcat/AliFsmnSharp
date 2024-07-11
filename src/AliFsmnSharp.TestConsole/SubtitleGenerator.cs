using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using AliFsmnSharp.Model;
using Antelcat.Media.Abstractions;
using Antelcat.Media.Decoders;
using EasyPathology.Abstractions.Interfaces;
using Sdcb.FFmpeg.Raw;

namespace AliFsmnSharp.TestConsole;

/// <summary>
/// 通过AI生成字幕
/// </summary>
public class SubtitleGenerator : IStateMachine<SubtitleGenerator.States>, INotifyPropertyChanged
{
    public ObservableCollection<TextSpan> Subtitles { get; } = [];

    public enum States
    {
        /// <summary>
        /// 准备好生成
        /// </summary>
        Standby,

        /// <summary>
        /// 正在探测音频中的有效区域
        /// </summary>
        Detecting,

        /// <summary>
        /// 对有效区域进行字幕生成
        /// </summary>
        Generating,

        /// <summary>
        /// 生成完成
        /// </summary>
        Done,

        Failed
    }

    public States CurrentState
    {
        get => current;
        set
        {
            if (current == value)
            {
                return;
            }

            StateChanging?.Invoke(current, value);
            current = value;
            OnPropertyChanged();
        }
    }

    private States current;

    public Exception? LastException { get; private set; }

    public event IStateMachine<States>.StateChangeHandler? StateChanging;
    
    public async Task Start(
        string filePath,
        bool enableVad,
        Func<TimeSpan> currentTimeGetter,
        CancellationToken cancellationToken)
    {
        try
        {
            if (current != States.Standby)
            {
                throw new InvalidOperationException("Invalid state: " + current);
            }

            var waveform = await Task.Run(() =>
                {
                    using var source  = new FFmpegUrlDecoderContext(filePath, AVMediaType.Audio);
                    using var decoder = new FFmpegAudioDecoder(source, new AudioFrameFormat(16000, 32, 1));
                    var       buffer  = new List<float>();
                    decoder.FrameDecoded += decodedAudioFrame =>
                        buffer.AddRange(decodedAudioFrame.AsSpan<float>().ToArray());
                    while (decoder.Decode(cancellationToken) == DecodeResult.Success)
                    {
                    }

                    return buffer.ToArray();
                },
                cancellationToken);

            if (waveform.Length == 0)
            {
                CurrentState = States.Done;
                return;
            }

            var queue = new TimeWindowQueue();
            if (enableVad)
            {
                await Task.WhenAll(
                    Task.Run(() => VadWork(queue, waveform, cancellationToken), cancellationToken),
                    Task.Run(() => AsrWork(queue, waveform, currentTimeGetter, cancellationToken), cancellationToken));
            }
            else
            {
                queue.Enqueue(new TimeWindow(TimeSpan.Zero, TimeSpan.MaxValue));
                queue.IsEnqueueFinished = true;
                await Task.Run(() => AsrWork(queue, waveform, currentTimeGetter, cancellationToken), cancellationToken);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            LastException = e;
            CurrentState  = States.Failed;
        }
    }

    public Task Start(
        float[] waveform,
        bool enableVad,
        Func<TimeSpan> currentTimeGetter,
        CancellationToken cancellationToken)
    {
        try
        {
            if (current != States.Standby)
            {
                throw new InvalidOperationException("Invalid state: " + current);
            }

            if (waveform.Length == 0)
            {
                throw new ArgumentException("Invalid waveform: empty");
            }

            var queue = new TimeWindowQueue();
            if (enableVad)
            {
                return Task.WhenAll(
                    Task.Run(() => VadWork(queue, waveform, cancellationToken), cancellationToken),
                    Task.Run(() => AsrWork(queue, waveform, currentTimeGetter, cancellationToken), cancellationToken));
            }

            queue.Enqueue(new TimeWindow(TimeSpan.Zero, TimeSpan.MaxValue));
            queue.IsEnqueueFinished = true;
            return Task.Run(() => AsrWork(queue, waveform, currentTimeGetter, cancellationToken), cancellationToken);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            LastException = e;
            CurrentState  = States.Failed;
        }

        return Task.CompletedTask;
    }

    private void VadWork(TimeWindowQueue queue, float[] waveform, CancellationToken cancellationToken)
    {
        CurrentState = States.Detecting;
        using var vad = new FsmnVad(LocalModels.FsmnVad, new FsmnVadOptions(2));
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        foreach (var timeWindow in vad.Inference(waveform))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            queue.Enqueue(timeWindow);
        }

        queue.IsEnqueueFinished = true;
        CurrentState            = States.Generating;
    }

    private void AsrWork(
        TimeWindowQueue queue,
        float[] waveform,
        Func<TimeSpan> currentTimeGetter,
        CancellationToken cancellationToken)
    {
        using var asr = new Paraformer(LocalModels.ParaformerQuant, new ParaformerOptions(2));
        while (!cancellationToken.IsCancellationRequested)
        {
            if (queue.TryDequeue(currentTimeGetter(), out var timeWindow))
            {
                // 计算开始和结束的样本索引
                var startSampleIndex =
                    (int)Math.Clamp(timeWindow.BeginTime.TotalSeconds * asr.SampleRate, 0, waveform.Length);
                var endSampleIndex =
                    (int)Math.Clamp(timeWindow.EndTime.TotalSeconds * asr.SampleRate, 0, waveform.Length);

                // 切割对应的浮点数数组
                var length  = endSampleIndex - startSampleIndex;
                var segment = new float[length];
                Array.Copy(waveform, startSampleIndex, segment, 0, length);

                var result = asr.Inference(new[] { segment })
                    .Select(s => s with
                    {
                        Begin = timeWindow.BeginTime + s.Begin,
                        End = timeWindow.BeginTime   + s.End
                    })
                    .ToList();

                foreach (var textSpan in result) Subtitles.Add(textSpan);
            }
            else if (queue.IsEnqueueFinished)
            {
                break;
            }
        }

        CurrentState = States.Done;
    }

    /// <summary>
    /// 读取字幕
    /// </summary>
    /// <param name="filePath">字幕文件路径</param>
    /// <returns></returns>
    public async Task ReadSrt(string filePath)
    {
        if (current != States.Standby)
        {
            throw new InvalidOperationException("Invalid state: " + current);
        }

        CurrentState = States.Generating;

        var lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);
        var index = 0;
        while (index < lines.Length)
        {
            var line = lines[index];
            if (int.TryParse(line, out _))
            {
                index++;
                var time      = lines[index];
                var times     = time.Split("-->");
                var startTime = TimeSpan.ParseExact(times[0].Trim(), @"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
                var endTime   = TimeSpan.ParseExact(times[1].Trim(), @"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
                index++;
                var content = new StringBuilder();
                while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
                {
                    content.AppendLine(lines[index]);
                    index++;
                }

                Subtitles.Add(new TextSpan(
                    content.ToString().Trim(),
                    startTime,
                    endTime));
            }

            index++;
        }

        CurrentState = States.Done;
    }

    public async Task SaveSrt(string filePath)
    {
        if (current != States.Done || Subtitles.Count == 0)
        {
            return;
        }

        var lines = new List<string>();
        foreach (var (i, subtitle) in Subtitles.Skip(1).Select((x, i) => (i, x)))
        {
            lines.Add(i.ToString());
            lines.Add($"{subtitle.Begin:hh\\:mm\\:ss\\,fff} --> {subtitle.End:hh\\:mm\\:ss\\,fff}");
            lines.Add(subtitle.Text);
            lines.Add("");
        }

        await File.WriteAllLinesAsync(filePath, lines, Encoding.UTF8);
    }

    private class TimeWindowQueue
    {
        public bool IsEnqueueFinished { get; set; }

        private readonly ManualResetEvent waitHandle = new(false);
        private readonly object           locker     = new();
        private          List<TimeWindow> queue      = [];

        //添加数据到队列
        public void Enqueue(TimeWindow data)
        {
            lock (locker)
            {
                queue.Add(data);
                queue = queue.OrderBy(d => d.BeginTime).ThenBy(d => d.EndTime).ToList();
            }

            waitHandle.Set();
        }

        //根据当前时间从队列中取出数据
        public bool TryDequeue(TimeSpan currentTime, out TimeWindow timeWindow)
        {
            waitHandle.WaitOne();
            lock (locker)
            {
                foreach (var d in queue)
                {
                    if (d.BeginTime <= currentTime && currentTime <= d.EndTime)
                    {
                        timeWindow = d;
                        queue.Remove(d);
                        if (queue.Count == 0 && !IsEnqueueFinished)
                        {
                            waitHandle.Reset();
                        }

                        return true;
                    }
                }

                foreach (var d in queue)
                {
                    if (d.BeginTime > currentTime)
                    {
                        timeWindow = d;
                        queue.Remove(d);
                        if (queue.Count == 0 && !IsEnqueueFinished)
                        {
                            waitHandle.Reset();
                        }

                        return true;
                    }
                }

                if (queue.Count > 0)
                {
                    timeWindow = queue[0];
                    queue.RemoveAt(0);
                    if (queue.Count == 0 && !IsEnqueueFinished)
                    {
                        waitHandle.Reset();
                    }

                    return true;
                }
            }

            timeWindow = default;
            return false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}