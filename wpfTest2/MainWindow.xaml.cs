using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace WpfVideoPlayer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly List<VideoPlayer> videoPlayers = new();
        private readonly ILogger<VideoPlayer> _logger;
        private readonly Grid videoGrid;
        private bool isUpdatingLayout = false;
        private readonly DispatcherTimer resizeTimer;
        private bool isClosing = false;

        // RTSP URL 列表
        private readonly List<string> rtspUrls = new()
        {
            "rtsp://admin:ruixin888888@192.168.0.214/h264/ch1/sub/av_stream",
            "rtsp://192.168.0.6:8554/s1",
            "rtsp://192.168.0.6:8554/s2",
            "rtsp://192.168.0.6:8554/s3",
            "rtsp://192.168.0.6:8554/s4",
            "rtsp://192.168.0.6:8554/s5",
            "rtsp://192.168.0.6:8554/s6",
            "rtsp://192.168.0.6:8554/s7",
            "rtsp://192.168.0.6:8554/s8",
            // 添加更多 URL...
        };

        public MainWindow(ILogger<VideoPlayer> logger)
        {
            InitializeComponent();
            _logger = logger;

            // 初始化视频网格
            videoGrid = new Grid();
            Content = videoGrid;

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            // 初始化调整大小的计时器
            resizeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500) // 500ms 延迟
            };
            resizeTimer.Tick += ResizeTimer_Tick;

            // 改用 SourceUpdated 事件而不是 SizeChanged
            SizeChanged += MainWindow_SizeChanged;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await UpdateVideoLayoutAsync();
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (isClosing || isUpdatingLayout) return;

            // 重置并重新启动计时器
            resizeTimer.Stop();
            resizeTimer.Start();
        }

        private async void ResizeTimer_Tick(object sender, EventArgs e)
        {
            resizeTimer.Stop();
            await UpdateVideoLayoutAsync();
        }

        private async Task UpdateVideoLayoutAsync()
        {
            if (isUpdatingLayout || isClosing) return;

            try
            {
                isUpdatingLayout = true;
                _logger.LogInformation("Starting layout update");

                int count = rtspUrls.Count;
                if (count == 0) return;

                // 计算新的网格布局
                int cols = (int)Math.Ceiling(Math.Sqrt(count));
                int rows = (int)Math.Ceiling((double)count / cols);

                // 如果布局没有变化，不进行更新
                if (videoGrid.RowDefinitions.Count == rows && 
                    videoGrid.ColumnDefinitions.Count == cols &&
                    videoPlayers.Count == count)
                {
                    return;
                }

                // 停止现有播放器
                var stopTasks = videoPlayers.Select(p => p.StopAsync()).ToList();
                await Task.WhenAll(stopTasks.ToArray());

                foreach (var player in videoPlayers)
                {
                    player.Dispose();
                }
                videoPlayers.Clear();

                // 清除现有布局
                videoGrid.Children.Clear();
                videoGrid.RowDefinitions.Clear();
                videoGrid.ColumnDefinitions.Clear();

                // 创建网格定义
                for (int i = 0; i < rows; i++)
                {
                    videoGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                }
                for (int i = 0; i < cols; i++)
                {
                    videoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }

                // 创建并启动新的播放器
                var startTasks = new List<Task>();
                for (int i = 0; i < count; i++)
                {
                    int row = i / cols;
                    int col = i % cols;

                    var container = new Border
                    {
                        BorderBrush = Brushes.Black,
                        BorderThickness = new Thickness(1),
                        Margin = new Thickness(2)
                    };

                    var image = new Image
                    {
                        Stretch = Stretch.Uniform
                    };

                    container.Child = image;
                    Grid.SetRow(container, row);
                    Grid.SetColumn(container, col);
                    videoGrid.Children.Add(container);

                    var player = new VideoPlayer(image, rtspUrls[i], _logger);
                    videoPlayers.Add(player);
                    startTasks.Add(player.StartAsync());

                    // 添加右键菜单
                    var contextMenu = new ContextMenu();
                    var menuItem = new MenuItem { Header = "全屏" };
                    menuItem.Click += (s, e) => ToggleFullScreen(container, player);
                    contextMenu.Items.Add(menuItem);
                    container.ContextMenu = contextMenu;
                }

                await Task.WhenAll(startTasks.ToArray());
                _logger.LogInformation("Layout update completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating video layout");
            }
            finally
            {
                isUpdatingLayout = false;
            }
        }

        private void ToggleFullScreen(Border container, VideoPlayer player)
        {
            if (container.Parent == videoGrid)
            {
                // 进入全屏
                videoGrid.Children.Remove(container);
                var fullscreenWindow = new Window
                {
                    WindowStyle = WindowStyle.None,
                    WindowState = WindowState.Maximized,
                    Content = container
                };
                fullscreenWindow.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Escape)
                    {
                        fullscreenWindow.Close();
                    }
                };
                fullscreenWindow.Closed += (s, e) =>
                {
                    fullscreenWindow.Content = null;
                    videoGrid.Children.Add(container);
                };
                fullscreenWindow.Show();
            }
        }

        private async void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (isClosing) 
            {
                // 如果已经在关闭过程中，不再取消关闭
                e.Cancel = false;
                return;
            }

            e.Cancel = true;
            isClosing = true;

            try
            {
                _logger.LogInformation("Starting window closing process");
                var stopTasks = videoPlayers.Select(p => p.StopAsync()).ToList();
                await Task.WhenAll(stopTasks);

                foreach (var player in videoPlayers)
                {
                    player.Dispose();
                }
                videoPlayers.Clear();
                _logger.LogInformation("All video players stopped and disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during window closing");
            }
            finally
            {
                isClosing = false;
                // 使用 Application.Current.Dispatcher 确保在主线程上执行
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        Application.Current.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during application shutdown");
                    }
                }), DispatcherPriority.Normal);
            }
        }

        // 添加/删除视频源的方法
        public void AddRtspUrl(string url)
        {
            if (!rtspUrls.Contains(url))
            {
                rtspUrls.Add(url);
                UpdateVideoLayoutAsync();
            }
        }

        public void RemoveRtspUrl(string url)
        {
            if (rtspUrls.Remove(url))
            {
                UpdateVideoLayoutAsync();
            }
        }
    }
}