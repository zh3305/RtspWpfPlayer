using Microsoft.Extensions.Logging;
using System.Windows.Controls;
using WpfVideoPlayer.Interfaces;

namespace WpfVideoPlayer.Factory
{
    public enum VideoPlayerType
    {
        Software,
        DirectX11
    }

    public class VideoPlayerFactory
    {
        private readonly ILoggerFactory _loggerFactory;

        public VideoPlayerFactory(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
        }

        public IVideoPlayer CreatePlayer(VideoPlayerType type, Image targetImage, string url)
        {
            return type switch
            {
                VideoPlayerType.Software => new VideoPlayer(
                    targetImage, 
                    url, 
                    _loggerFactory.CreateLogger<VideoPlayer>()),
                    
                VideoPlayerType.DirectX11 => new DX11VideoPlayer(
                    targetImage, 
                    url, 
                    _loggerFactory.CreateLogger<DX11VideoPlayer>()),
                    
                _ => throw new ArgumentException("Unsupported player type", nameof(type))
            };
        }
    }
} 