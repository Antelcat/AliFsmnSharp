namespace AliFsmnSharp.Model;

internal class VadInputEntity {
    public float[]       Speech       { get; set; } = [];
    public int           SpeechLength { get; set; }
    public List<float[]> InCaches     { get; set; } = [];
    public float[]? Waveform { get; set; }
}