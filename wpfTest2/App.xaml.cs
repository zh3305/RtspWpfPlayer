using System.Windows;
using FFmpeg.AutoGen;

namespace WpfVideoPlayer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        static App()
        {
            try
            {
                // string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg", "x64");
                // if (!Directory.Exists(ffmpegPath))
                // {
                //     throw new DirectoryNotFoundException($"FFmpeg directory not found at: {ffmpegPath}");
                // }
                FFmpegBinariesHelper.RegisterFFmpegBinaries();

                Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");

                // 初始化 FFmpeg
                ffmpeg.avdevice_register_all();
                ffmpeg.avformat_network_init();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize FFmpeg: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown();
            }
        }
    }

}
