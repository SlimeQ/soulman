using System.Collections.Concurrent;

namespace Soulman;

public class TransferProgressBroker
{
    public event EventHandler<TransferProgress>? ProgressChanged;

    public void Report(string fileName, long bytesTransferred, long totalBytes)
    {
        ProgressChanged?.Invoke(this, new TransferProgress(fileName, bytesTransferred, totalBytes));
    }

    public void ReportCompletion(string fileName)
    {
        ProgressChanged?.Invoke(this, new TransferProgress(fileName, 100, 100) { IsComplete = true });
    }
}

public class TransferProgress
{
    public TransferProgress(string fileName, long bytesTransferred, long totalBytes)
    {
        FileName = fileName;
        BytesTransferred = bytesTransferred;
        TotalBytes = totalBytes;
        if (totalBytes > 0)
        {
            Percentage = (double)bytesTransferred / totalBytes * 100.0;
        }
    }

    public string FileName { get; }
    public long BytesTransferred { get; }
    public long TotalBytes { get; }
    public double Percentage { get; }
    public bool IsComplete { get; init; }
}
