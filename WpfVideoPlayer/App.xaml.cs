using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FFmpeg.AutoGen;
using System.Windows;
using Microsoft.Extensions.Logging.Console;

namespace WpfVideoPlayer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private ServiceProvider serviceProvider;

        static App()
        {
            try
            {
                FFmpegBinariesHelper.RegisterFFmpegBinaries();
                Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");
                ffmpeg.avdevice_register_all();
                ffmpeg.avformat_network_init();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize FFmpeg: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown();
            }
        }

        public App()
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            serviceProvider = services.BuildServiceProvider();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(builder =>
            {
                builder.AddConsole(options =>
                {
                    options.FormatterName = "CustomFormatter";
                });

                builder.Services.Configure<ConsoleFormatterOptions>(options =>
                {
                    options.IncludeScopes = true;
                    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";
                });

                builder.AddDebug();
            });

            // 注册自定义格式化器
            services.AddSingleton<ConsoleFormatter, CustomConsoleFormatter>();

            // 注册主窗口
            services.AddSingleton<MainWindow>();
            // 注册其他服务...
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);


            // var mainWindow = new MainWindow(serviceProvider.GetRequiredService<ILoggerFactory>());
            var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            
            if (serviceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
