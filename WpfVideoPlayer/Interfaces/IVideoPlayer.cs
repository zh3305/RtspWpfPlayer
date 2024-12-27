using System;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace WpfVideoPlayer.Interfaces
{
    public interface IVideoPlayer : IDisposable
    {
        bool IsPlaying { get; }
        string Url { get; }
        
        Task StartAsync();
        Task StopAsync();
        
        event EventHandler<Exception> OnError;
        event EventHandler<string> OnStatusChanged;
    }
} 