using System.Text;

namespace AliFsmnSharp.Model;

public static class LocalModels
{
    public static FsmnVadModel FsmnVad => new(
        Embedded.Vad_Model,
        Encoding.Default.GetString(Embedded.Vad_Yaml),
        Encoding.Default.GetString(Embedded.Vad_Mvn));

    public static ParaformerModel ParaformerQuant => new(
        Embedded.Paraformer_Large_Model_Quant,
        Encoding.Default.GetString(Embedded.Paramformer_Yaml),
        Encoding.Default.GetString(Embedded.Paramformer_Mvn)
    );
}