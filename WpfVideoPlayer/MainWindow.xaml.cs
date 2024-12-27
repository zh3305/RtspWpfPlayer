using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using WpfVideoPlayer.Factory;
using WpfVideoPlayer.Interfaces;

namespace WpfVideoPlayer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly List<IVideoPlayer> videoPlayers = new();
        private readonly ILoggerFactory _loggerFactory;
        private readonly VideoPlayerFactory _playerFactory;
        private readonly Grid mainGrid;
        private readonly Grid videoGrid;
        private bool isUpdatingLayout = false;
        private readonly DispatcherTimer resizeTimer;
        private bool isClosing = false;

        // RTSP URL 列表
        private readonly List<string> rtspUrls = new()
        {
            "rtsp://admin:ruixin888888@192.168.0.214/h264/ch1/sub/av_stream",
            // "rtsp://192.168.0.6:8554/s1",
            // "rtsp://192.168.0.6:8554/s2",
            // "rtsp://192.168.0.6:8554/s3",
            // "rtsp://192.168.0.6:8554/s4",
            // "rtsp://192.168.0.6:8554/s5",
            // "rtsp://192.168.0.6:8554/s6",
            // "rtsp://192.168.0.6:8554/s7",
            // "rtsp://192.168.0.6:8554/s8",
            // 添加更多 URL...
        };

        // 当前使用的播放器类型
        private VideoPlayerType _currentPlayerType = VideoPlayerType.DirectX11;

        public MainWindow(ILoggerFactory loggerFactory)
        {
            InitializeComponent();
            _loggerFactory = loggerFactory;
            _playerFactory = new VideoPlayerFactory(loggerFactory);

            // 初始化主网格
            mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 初始化视频网格
            videoGrid = new Grid();
            Grid.SetRow(videoGrid, 1);
            mainGrid.Children.Add(videoGrid);

            // 设置主窗口内容
            Content = mainGrid;

            // 添加事件处理
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            // 初始化调整大小的计时器
            resizeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            resizeTimer.Tick += ResizeTimer_Tick;

            SizeChanged += MainWindow_SizeChanged;

            // 添加播放器类型选择菜单
            CreatePlayerTypeMenu();
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

                    // 使用工厂创建播放器
                    var player = _playerFactory.CreatePlayer(_currentPlayerType, image, rtspUrls[i]);
                    videoPlayers.Add(player);
                    startTasks.Add(player.StartAsync());

                    // 添加右键菜单
                    var contextMenu = new ContextMenu();
                    var menuItem = new MenuItem { Header = "全屏" };
                    menuItem.Click += (s, e) => ToggleFullScreen(container);
                    contextMenu.Items.Add(menuItem);
                    container.ContextMenu = contextMenu;
                }

                await Task.WhenAll(startTasks.ToArray());
            }
            catch (Exception ex)
            {
                _loggerFactory.CreateLogger<MainWindow>().LogError(ex, "Error updating video layout");
            }
            finally
            {
                isUpdatingLayout = false;
            }
        }

        private void ToggleFullScreen(Border container)
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
                // 如果已经在关闭过程中，不再取关闭
                e.Cancel = false;
                return;
            }

            e.Cancel = true;
            isClosing = true;

            try
            {
                _loggerFactory.CreateLogger<MainWindow>().LogInformation("Starting window closing process");
                var stopTasks = videoPlayers.Select(p => p.StopAsync()).ToList();
                await Task.WhenAll(stopTasks);

                foreach (var player in videoPlayers)
                {
                    player.Dispose();
                }
                videoPlayers.Clear();
                _loggerFactory.CreateLogger<MainWindow>().LogInformation("All video players stopped and disposed");
            }
            catch (Exception ex)
            {
                _loggerFactory.CreateLogger<MainWindow>().LogError(ex, "Error during window closing");
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
                        _loggerFactory.CreateLogger<MainWindow>().LogError(ex, "Error during application shutdown");
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

        private void CreatePlayerTypeMenu()
        {
            var menu = new Menu();
            var playerTypeMenuItem = new MenuItem { Header = "播放器类型" };
            
            foreach (VideoPlayerType type in Enum.GetValues(typeof(VideoPlayerType)))
            {
                var item = new MenuItem 
                { 
                    Header = type.ToString(),
                    IsCheckable = true,
                    IsChecked = type == _currentPlayerType
                };
                
                item.Click += async (s, e) => 
                {
                    if (type != _currentPlayerType)
                    {
                        _currentPlayerType = type;
                        await UpdateVideoLayoutAsync();
                    }
                };
                
                playerTypeMenuItem.Items.Add(item);
            }
            
            menu.Items.Add(playerTypeMenuItem);
            
            // 将菜单添加到主网格的第一行
            Grid.SetRow(menu, 0);
            mainGrid.Children.Add(menu);
        }

    }
}