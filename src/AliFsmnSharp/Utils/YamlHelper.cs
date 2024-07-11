﻿using YamlDotNet.Serialization;

namespace AliFsmnSharp.Utils;

internal static class YamlHelper
{
    public static T? ReadYamlFile<T>(string yamlFilePath) => 
        !File.Exists(yamlFilePath) ? default : ReadYamlStream<T>(File.OpenText(yamlFilePath));

    public static T ReadYamlStream<T>(TextReader yamlReader, bool close = true)
    {
        var info = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build().Deserialize<T>(yamlReader);
        if (close) yamlReader.Close();
        return info;
    }

    public static T ReadYamlText<T>(string yamlText) =>
        new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<T>(yamlText);
}