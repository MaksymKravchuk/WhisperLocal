namespace WhisperLocal.Stt;

/// <summary>The Whisper model file is missing; the message tells the user how to get it.</summary>
public sealed class ModelNotFoundException(string modelPath) : FileNotFoundException(BuildMessage(modelPath), modelPath)
{
    public const string DownloadUrlBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    private static string BuildMessage(string modelPath)
    {
        var fileName = Path.GetFileName(modelPath);
        return $"""
            Whisper model not found:
            {modelPath}

            Download {fileName} (about 3 GB for large-v3) and put it into:
            {Path.GetDirectoryName(modelPath)}

            Either run scripts\get-model.ps1 from the repository,
            or download it manually from:
            {DownloadUrlBase}{fileName}
            """;
    }
}
