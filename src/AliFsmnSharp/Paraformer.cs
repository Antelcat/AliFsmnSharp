﻿using AliFsmnSharp.Model;
using AliFsmnSharp.Utils;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AliFsmnSharp;

/// <summary>
/// Fast and Accurate Parallel Transformer for Non-autoregressive End-to-End Speech Recognition
/// </summary>
/// <remarks>
/// 输入的音频需要是<see cref="SampleRate"/>采样率的单声道32位浮点型PCM音频
/// </remarks>
public sealed class Paraformer : IDisposable
{
    private readonly AsrYamlEntity    config;
    private readonly WavFrontend      frontend;
    private readonly TokenIdConverter converter;
    private readonly InferenceSession session;
    private readonly int              batchSize;

    public int SampleRate => config.frontend_conf.fs;

    public Paraformer(ParaformerModel model, ParaformerOptions? options = null)
    {
        options ??= new();
        config = YamlHelper.ReadYamlText<AsrYamlEntity>(model.Yaml)
                 ?? throw new InvalidDataException("Invalid config");
        converter = new TokenIdConverter(config.token_list);
        frontend  = new WavFrontend(model.Mvn.Split('\n'), config.frontend_conf);
        batchSize = options.BatchSize;
        session = new InferenceSession(model.Model, new SessionOptions
        {
            IntraOpNumThreads = options.IntraOpNumThreads
        });
    }

    /// <summary>
    /// 推理，生成字幕
    /// </summary>
    /// <param name="samplesList"></param>
    public IEnumerable<TextSpan> Inference(IEnumerable<float[]> samplesList)
    {
        foreach (var samplesBatch in samplesList.Chunk(batchSize))
        {
            var (feats, featsCount) = frontend.ExtractFeat(samplesBatch);

            var speechDim = session.InputMetadata["speech"].Dimensions;
            speechDim[0] = samplesBatch.Length;
            speechDim[1] = feats.Length / samplesBatch.Length / speechDim[2];

            var speechLengthsDim = session.InputMetadata["speech_lengths"].Dimensions;
            speechLengthsDim[0] = featsCount.Length;

            List<DisposableNamedOnnxValue> outputs;
            try
            {
                outputs = session.Run([
                    NamedOnnxValue.CreateFromTensor("speech",
                        new DenseTensor<float>(feats, speechDim)),
                    NamedOnnxValue.CreateFromTensor("speech_lengths",
                        new DenseTensor<int>(featsCount, speechLengthsDim)),
                ]).ToList();
            }
            catch (OnnxRuntimeException)
            {
                yield break;
            }

            var amScores = outputs[0].AsTensor<float>().ToDenseTensor();
            var predicts = Decode(
                amScores.Buffer,
                amScores.Dimensions,
                outputs[1].AsTensor<int>().ToArray());
            var usPeaks = outputs[3].AsTensor<float>().ToDenseTensor();
            var usPackSliceLength = (int)usPeaks.Length / usPeaks.Dimensions[0];
            foreach (var (i, predict) in predicts.Select((x,i)=> (i,x)))
            {
                var usPeak = usPeaks.Buffer.Slice(i * usPackSliceLength, usPackSliceLength);
                var timeRanges = TimestampLfr6Onnx(usPeak, predict);
               
                // 根据时间戳进行断句
                var beginIndex = 0;
                foreach (var (j, timeRange) in timeRanges.Select((x, d) => (d, x)))
                {
                    if (j >= predict.Length - 1)
                    {
                        // 剩余的全部合并
                        yield return GetAccurateTextSpan(predict.Length - 1);
                    }
                    else if (j > beginIndex)
                    {
                        var duration = (timeRanges[j + 1].BeginTime - timeRange.BeginTime).Ticks;
                        var averageDuration = timeRanges.Skip(beginIndex).Take(j - beginIndex + 1)
                            .CurrentAndNext().Average(pair => (pair.next.BeginTime - pair.current.BeginTime).Ticks);
                        if (duration > averageDuration * 2)
                        {
                            yield return GetAccurateTextSpan(j);
                            beginIndex = j + 1;
                        }
                    }
                }

                AccurateTextSpan GetAccurateTextSpan(int endIndex) =>
                    new(Enumerable
                        .Range(beginIndex, endIndex - beginIndex + 1)
                        .Select(index => new TextSpan(predict[index].Replace("@@", ""), timeRanges[index].BeginTime, timeRanges[index].EndTime))
                        .ToList());
            }
        }
    }

    private List<string[]> Decode(
        Memory<float> amScores,
        ReadOnlySpan<int> amScoreDimensions,
        int[] validTokenLengths)
    {
        var results = new List<string[]>();

        // amScores: [batchSize, x, y]
        for (var i = 0; i < amScoreDimensions[0]; i++)
        {
            var sliceLength = amScoreDimensions[1] * amScoreDimensions[2];
            results.Add(DecodeOne(
                amScores.Slice(i * sliceLength, sliceLength),
                amScoreDimensions[1..],
                validTokenLengths[i]));
        }

        return results;
    }

    private string[] DecodeOne(Memory<float> amScore, ReadOnlySpan<int> amScoreDimensions, int validTokenNum)
    {
        var ySeq = GetMaxIndexes(amScore, amScoreDimensions);

        // pad with mask tokens to ensure compatibility with sos/eos tokens
        // asr_model.sos:1  asr_model.eos:2
        var ySeqPadded = new int[ySeq.Length + 2];
        ySeqPadded[0] = 1;
        Array.Copy(ySeq, 0, ySeqPadded, 1, ySeq.Length);
        ySeqPadded[^1] = 2;

        // remove sos/eos and get results
        var tokenInt = new int[ySeqPadded.Length - 2];
        Array.Copy(ySeqPadded, 1, tokenInt, 0, tokenInt.Length);

        // remove blank symbol id, which is assumed to be 0
        tokenInt = tokenInt.Where(x => x != 0 && x != 2).ToArray();

        // Change integer-ids to tokens
        var tokens = converter.Ids2Tokens(tokenInt); // You'll need to implement this
        tokens = tokens[..(validTokenNum - config.model_conf.predictor_bias)];

        return tokens;
    }

    private static int[] GetMaxIndexes(Memory<float> arrays, ReadOnlySpan<int> dimensions)
    {
        var x = dimensions[0];
        var y = dimensions[1];

        var result = new int[x];
        for (var i = 0; i < x; i++)
        {
            var slice = arrays.Slice(i * y, y);

            var max = slice.Span[0];
            var maxIndex = 0;
            for (var j = 1; j < y; j++)
            {
                if (!(slice.Span[j] > max)) continue;
                max = slice.Span[j];
                maxIndex = j;
            }

            result[i] = maxIndex;
        }

        return result;
    }

    private readonly record struct TimeRange(TimeSpan BeginTime, TimeSpan EndTime);

    private static List<TimeRange> TimestampLfr6Onnx(
        Memory<float> usPeaks,
        string[] rawTokens,
        float beginTime = 0.0f,
        float totalOffset = -1.5f)
    {
        const int StartEndThreshold = 5;
        const int MaxTokenDuration = 30;
        const double TimeRate = 10.0 * 6 / 1000 / 3;

        var numFrames = usPeaks.Length;

        var newCharList = new List<string>();
        var timestampList = new List<TimeRange>();

        var firePlace = usPeaks.Span.ToArray()
            .Select((value, index) => value > 1.0 - 1e-4 ? index : -1)
            .Where(index => index != -1)
            .Select(index => index + (int)totalOffset)
            .ToList();

        if (firePlace[0] > StartEndThreshold)
        {
            timestampList.Add(new TimeRange(TimeSpan.FromSeconds(0.0),
                TimeSpan.FromSeconds(firePlace[0] * TimeRate)));
            newCharList.Add("<sil>");
        }

        for (var i = 0; i < firePlace.Count - 1; i++)
        {
            newCharList.Add(rawTokens[i]);

            if (i == firePlace.Count - 2 ||
                firePlace[i + 1] - firePlace[i] < MaxTokenDuration)
            {
                timestampList.Add(new TimeRange(TimeSpan.FromSeconds(firePlace[i] * TimeRate),
                    TimeSpan.FromSeconds(firePlace[i + 1] * TimeRate)));
            }
            else
            {
                var split = firePlace[i] + MaxTokenDuration;

                timestampList.Add(new TimeRange(TimeSpan.FromSeconds(firePlace[i] * TimeRate),
                    TimeSpan.FromSeconds(split * TimeRate)));
                timestampList.Add(new TimeRange(TimeSpan.FromSeconds(split * TimeRate),
                    TimeSpan.FromSeconds(firePlace[i + 1] * TimeRate)));

                newCharList.Add("<sil>");
            }
        }

        if (numFrames - firePlace.Last() > StartEndThreshold)
        {
            var end = (numFrames + firePlace.Last()) / 2.0;

            var lastWindow = timestampList[^1];
            timestampList[^1] = lastWindow with { EndTime = TimeSpan.FromSeconds(end * TimeRate) };
            timestampList.Add(new TimeRange(TimeSpan.FromSeconds(end * TimeRate),
                TimeSpan.FromSeconds(numFrames * TimeRate)));

            newCharList.Add("<sil>");
        }
        else
        {
            var lastWindow = timestampList[^1];
            timestampList[^1] = lastWindow with { EndTime = TimeSpan.FromSeconds(numFrames * TimeRate) };
        }

        if (!(Math.Abs(beginTime) > 1e-6))
        {
            return timestampList.Where((_, index) => newCharList[index] != "<sil>").ToList();
        }


        for (var i = 0; i < timestampList.Count; i++)
        {
            var window = timestampList[i];
            timestampList[i] = new TimeRange(window.BeginTime + TimeSpan.FromSeconds(beginTime / 1000.0),
                window.EndTime + TimeSpan.FromSeconds(beginTime / 1000.0));
        }

        return timestampList.Where((_, index) => newCharList[index] != "<sil>").ToList();
    }


    public void Dispose()
    {
        session.Dispose();
    }
}

file static class LinqExtension
{
    public static IEnumerable<(T current, T next)> CurrentAndNext<T>(this IEnumerable<T> source)
    {
        using var enumerator = source.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            yield break;
        }

        var previous = enumerator.Current;
        while (enumerator.MoveNext())
        {
            yield return (previous, enumerator.Current);
            previous = enumerator.Current;
        }
    }
}