namespace FlyLeafWithDownload.Download
{
    public interface IDownloader : IDisposable
    {
        event EventHandler<double>? ProgressChanged;
        event EventHandler<string>? Completed;
        event EventHandler<Exception?>? Failed;

        void Cancel();
        Task StartAsync(string url);
    }
}
