using FFmpeg.AutoGen;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SharpDX.Mathematics.Interop;
using Buffer = System.Buffer;
using D3D11Buffer = SharpDX.Direct3D11.Buffer;
using D3D11MapFlags = SharpDX.Direct3D11.MapFlags;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;

namespace wpfTest2
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public unsafe partial class MainWindow : Window, IDisposable
    {
        private SharpDX.Direct3D11.Device d3dDevice;
        private DeviceContext d3dContext;
        private SwapChain swapChain;
        private Texture2D renderTarget;
        private RenderTargetView renderTargetView;
        private Texture2D renderTexture;
        private ShaderResourceView textureView;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private Task playbackTask;
        private bool disposed;
        private bool isPlaying = true;
        private delegate AVPixelFormat GetFormatDelegate(AVCodecContext* ctx, AVPixelFormat* fmt);
        private GetFormatDelegate getFormatCallback;
        private GCHandle getFormatCallbackHandle;
        private WriteableBitmap currentBitmap;
        private readonly object bitmapLock = new object();
        private bool isTestMode = false; // 添加测试模式标志

        public MainWindow()
        {
            InitializeComponent();
            InitializeVideoDisplay();
            InitializeD3D();
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (isTestMode)
            {
                StartColorTest();
            }
            else
            {
                Console.WriteLine("Window loaded, starting playback...");
                playbackTask = Task.Run(() => PlaybackLoop(cts.Token));
            }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            cts.Cancel();
            playbackTask?.Wait();
            Dispose();
        }

        private void InitializeD3D()
        {
            Console.WriteLine("Initializing Direct3D...");
            var desc = new SwapChainDescription()
            {
                BufferCount = 2,
                ModeDescription = new ModeDescription(1920, 1080, new Rational(60, 1), Format.B8G8R8A8_UNorm),
                IsWindowed = true,
                OutputHandle = new WindowInteropHelper(this).EnsureHandle(),
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipSequential,
                Usage = Usage.RenderTargetOutput
            };

            SharpDX.Direct3D11.Device device;
            SwapChain chain;
            SharpDX.Direct3D11.Device.CreateWithSwapChain(
                SharpDX.Direct3D.DriverType.Hardware,
                DeviceCreationFlags.None,
                desc,
                out device,
                out chain);

            d3dDevice = device;
            swapChain = chain;
            renderTarget = Texture2D.FromSwapChain<Texture2D>(swapChain, 0);
            renderTargetView = new RenderTargetView(d3dDevice, renderTarget);
            d3dContext = d3dDevice.ImmediateContext;
            Console.WriteLine("Direct3D initialized successfully");
        }

        private void EnsureRenderTexture(int width, int height)
        {
            if (renderTexture == null || renderTexture.Description.Width != width || renderTexture.Description.Height != height)
            {
                Console.WriteLine($"Creating new render texture: {width}x{height}");
                renderTexture?.Dispose();
                textureView?.Dispose();

                renderTexture = new Texture2D(d3dDevice, new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    CpuAccessFlags = CpuAccessFlags.None,
                    OptionFlags = ResourceOptionFlags.None
                });

                textureView = new ShaderResourceView(d3dDevice, renderTexture);
            }
        }

        private Texture2D CreateStagingTexture(int width, int height)
        {
            return new Texture2D(d3dDevice, new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None
            });
        }

        private unsafe void RenderFrame(AVFrame* frame)
        {
            try
            {
                Console.WriteLine($"Frame format: {frame->format}");
                Console.WriteLine($"Frame dimensions: {frame->width}x{frame->height}");
                Console.WriteLine($"Frame linesize: [{frame->linesize[0]}, {frame->linesize[1]}, {frame->linesize[2]}]");

                // 使用软件解码渲染
                RenderSoftwareFrame(frame);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Render error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private unsafe void RenderSoftwareFrame(AVFrame* frame)
        {
            try
            {
                Console.WriteLine("Processing software decoded frame...");

                // 使用 Invoke 而不是 BeginInvoke 来确保帧按顺序处理
                Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
                {
                    try
                    {
                        lock (bitmapLock)
                        {
                            // 检查帧尺寸是否有效
                            if (frame->width <= 0 || frame->height <= 0)
                            {
                                Console.WriteLine($"Invalid frame dimensions: {frame->width}x{frame->height}");
                                return;
                            }

                            // 检查是否需要重新创建 WriteableBitmap
                            if (currentBitmap == null || 
                                currentBitmap.PixelWidth != frame->width || 
                                currentBitmap.PixelHeight != frame->height)
                            {
                                try
                                {
                                    var newBitmap = new WriteableBitmap(
                                        frame->width,
                                        frame->height,
                                        96,
                                        96,
                                        PixelFormats.Bgra32,
                                        null);

                                    currentBitmap = newBitmap;
                                    videoImage.Source = currentBitmap;
                                    Console.WriteLine($"Created new bitmap: {frame->width}x{frame->height}");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Failed to create bitmap: {ex.Message}");
                                    return;
                                }
                            }

                            try
                            {
                                currentBitmap.Lock();

                                Console.WriteLine($"Converting YUV420P to BGRA... Frame timestamp: {frame->pts}");
                                YUV420PtoBGRA(
                                    frame->data[0], frame->linesize[0],
                                    frame->data[1], frame->linesize[1],
                                    frame->data[2], frame->linesize[2],
                                    (byte*)currentBitmap.BackBuffer,
                                    currentBitmap.BackBufferStride,
                                    frame->width,
                                    frame->height);

                                currentBitmap.AddDirtyRect(new Int32Rect(0, 0, frame->width, frame->height));
                                Console.WriteLine($"Frame {frame->pts} updated successfully");
                            }
                            finally
                            {
                                if (currentBitmap != null)
                                {
                                    currentBitmap.Unlock();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error updating image: {ex.Message}\n{ex.StackTrace}");
                    }
                }));

                Console.WriteLine($"Frame {frame->pts} render completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Software render error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private unsafe void YUV420PtoBGRA(
            byte* y, int yStride,
            byte* u, int uStride,
            byte* v, int vStride,
            byte* bgra, int bgraStride,
            int width, int height)
        {
            // YUV420P to RGB conversion coefficients (BT.601)
            const int C_Y = 298;
            const int C_U_B = 517;
            const int C_U_G = -100;
            const int C_V_G = -208;
            const int C_V_R = 409;
            const int C_SHIFT = 8;

            Console.WriteLine($"Converting YUV420P to BGRA: {width}x{height}");
            Console.WriteLine($"Strides - Y: {yStride}, U: {uStride}, V: {vStride}, BGRA: {bgraStride}");

            for (int j = 0; j < height; j++)
            {
                byte* yLine = y + j * yStride;
                byte* uLine = u + (j >> 1) * uStride;
                byte* vLine = v + (j >> 1) * vStride;
                byte* bgraLine = bgra + j * bgraStride;

                for (int i = 0; i < width; i++)
                {
                    int Y = yLine[i];
                    int U = uLine[i >> 1] - 128;
                    int V = vLine[i >> 1] - 128;

                    Y = (Y - 16) * C_Y;

                    int R = (Y + C_V_R * V + 128) >> C_SHIFT;
                    int G = (Y + C_U_G * U + C_V_G * V + 128) >> C_SHIFT;
                    int B = (Y + C_U_B * U + 128) >> C_SHIFT;

                    // Clamp values
                    R = R < 0 ? 0 : R > 255 ? 255 : R;
                    G = G < 0 ? 0 : G > 255 ? 255 : G;
                    B = B < 0 ? 0 : B > 255 ? 255 : B;

                    bgraLine[i * 4 + 0] = (byte)B;  // B
                    bgraLine[i * 4 + 1] = (byte)G;  // G
                    bgraLine[i * 4 + 2] = (byte)R;  // R
                    bgraLine[i * 4 + 3] = 255;      // A
                }
            }
            Console.WriteLine("YUV to BGRA conversion completed");
        }

        public void Dispose()
        {
            if (!disposed)
            {
                if (getFormatCallbackHandle.IsAllocated)
                {
                    getFormatCallbackHandle.Free();
                }

                cts.Dispose();
                textureView?.Dispose();
                renderTexture?.Dispose();
                renderTargetView?.Dispose();
                renderTarget?.Dispose();
                swapChain?.Dispose();
                d3dContext?.Dispose();
                d3dDevice?.Dispose();
                disposed = true;
            }
        }

        private unsafe void PlaybackLoop(CancellationToken token)
        {
            Console.WriteLine("Starting playback loop...");
            AVFormatContext* formatContext = null;
            AVCodecContext* codecContext = null;
            AVFrame* frame = null;
            AVPacket* packet = null;

            try
            {
                var url = "rtsp://admin:ruixin888888@192.168.0.214/h264/ch1/sub/av_stream";
                Console.WriteLine($"Opening RTSP stream: {url}");
                
                int result = ffmpeg.avformat_open_input(&formatContext, url, null, null);
                if (result < 0)
                {
                    Console.WriteLine($"Failed to open input: {result}");
                    throw new FFmpegException("Failed to open input", result);
                }

                Console.WriteLine("Stream opened successfully");
                int streamInfoResult = ffmpeg.avformat_find_stream_info(formatContext, null);
                if (streamInfoResult < 0)
                {
                    Console.WriteLine($"Failed to find stream info: {streamInfoResult}");
                    throw new FFmpegException("Failed to find stream info", streamInfoResult);
                }

                var videoStream = FindVideoStream(formatContext);
                Console.WriteLine($"Found video stream at index: {videoStream}");

                var codec = SetupCodec(formatContext, videoStream, out codecContext);
                Console.WriteLine($"Using codec: {Marshal.PtrToStringAnsi((IntPtr)codec->name)}");

                ConfigureHardwareAcceleration(codecContext);

                Console.WriteLine("Opening codec...");
                if (ffmpeg.avcodec_open2(codecContext, codec, null) < 0)
                {
                    throw new InvalidOperationException("Failed to open codec");
                }

                frame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();

                Console.WriteLine("Starting frame reading loop...");
                while (!token.IsCancellationRequested)
                {
                    if (ffmpeg.av_read_frame(formatContext, packet) < 0) break;

                    if (packet->stream_index == videoStream)
                    {
                        Console.WriteLine($"Got video packet, size: {packet->size}");
                        ProcessVideoPacket(codecContext, frame, packet);
                    }

                    ffmpeg.av_packet_unref(packet);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex}");
            }
            finally
            {
                if (frame != null)
                {
                    var tempFrame = frame;
                    ffmpeg.av_frame_free(&tempFrame);
                }
                if (packet != null)
                {
                    var tempPacket = packet;
                    ffmpeg.av_packet_free(&tempPacket);
                }
                if (codecContext != null)
                {
                    var tempCodecContext = codecContext;
                    ffmpeg.avcodec_free_context(&tempCodecContext);
                }
                if (formatContext != null)
                {
                    var tempFormatContext = formatContext;
                    ffmpeg.avformat_close_input(&tempFormatContext);
                }
            }
        }

        private unsafe int FindVideoStream(AVFormatContext* formatContext)
        {
            for (int i = 0; i < formatContext->nb_streams; i++)
            {
                if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    return i;
                }
            }
            throw new InvalidOperationException("No video stream found");
        }

        private unsafe AVCodec* SetupCodec(AVFormatContext* formatContext, int videoStream, out AVCodecContext* codecContext)
        {
            var codecParams = formatContext->streams[videoStream]->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
            if (codec == null)
            {
                throw new InvalidOperationException("Codec not found");
            }

            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
            {
                throw new InvalidOperationException("Failed to allocate codec context");
            }

            if (ffmpeg.avcodec_parameters_to_context(codecContext, codecParams) < 0)
            {
                throw new InvalidOperationException("Failed to copy codec params to codec context");
            }

            return codec;
        }

        private unsafe void ConfigureHardwareAcceleration(AVCodecContext* codecContext)
        {
            // 暂时禁用硬件加速，使用软件解码
            codecContext->hw_device_ctx = null;

            // 创建并保持对委的引用
            getFormatCallback = (ctx, fmt) =>
            {
                Console.WriteLine("Using software decoding");
                return AVPixelFormat.AV_PIX_FMT_YUV420P;
            };

            // 固定委托防止被垃圾回收
            getFormatCallbackHandle = GCHandle.Alloc(getFormatCallback);

            // 将委托转换为函数指针并赋值给 get_format
            codecContext->get_format = new AVCodecContext_get_format_func 
            { 
                Pointer = Marshal.GetFunctionPointerForDelegate(getFormatCallback)
            };
        }

        private unsafe void ProcessVideoPacket(AVCodecContext* codecContext, AVFrame* frame, AVPacket* packet)
        {
            int ret = ffmpeg.avcodec_send_packet(codecContext, packet);
            if (ret < 0)
            {
                Console.WriteLine($"Error sending packet for decoding: {ret}");
                return;
            }

            while (ret >= 0)
            {
                ret = ffmpeg.avcodec_receive_frame(codecContext, frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    break;
                else if (ret < 0)
                {
                    Console.WriteLine($"Error during decoding: {ret}");
                    break;
                }

                Console.WriteLine($"Decoded frame: {frame->width}x{frame->height}, format: {frame->format}, pts: {frame->pts}");
                RenderFrame(frame);

                // 添加一个小延迟以控制帧率
                Thread.Sleep(1); // 1ms delay
            }
        }

        private void InitializeVideoDisplay()
        {
            try
            {
                // 预先创建 WriteableBitmap，使用较小的初始尺寸
                currentBitmap = new WriteableBitmap(
                    16,  // 最小宽度
                    16,  // 最小高度
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null);

                videoImage.Source = currentBitmap;
                Console.WriteLine("Video display initialized");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize video display: {ex.Message}");
            }
        }

        private void StartColorTest()
        {
            var colors = new byte[][] {
                new byte[] { 255, 0, 0, 255 },    // Red
                new byte[] { 0, 255, 0, 255 },    // Green
                new byte[] { 0, 0, 255, 255 },    // Blue
                new byte[] { 255, 255, 0, 255 },  // Yellow
                new byte[] { 0, 255, 255, 255 },  // Cyan
                new byte[] { 255, 0, 255, 255 }   // Magenta
            };
            int colorIndex = 0;

            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (s, e) =>
            {
                lock (bitmapLock)
                {
                    try
                    {
                        currentBitmap.Lock();
                        var color = colors[colorIndex];
                        var buffer = (byte*)currentBitmap.BackBuffer;
                        var stride = currentBitmap.BackBufferStride;
                        var width = currentBitmap.PixelWidth;
                        var height = currentBitmap.PixelHeight;

                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                var pixel = buffer + y * stride + x * 4;
                                pixel[0] = color[0]; // B
                                pixel[1] = color[1]; // G
                                pixel[2] = color[2]; // R
                                pixel[3] = color[3]; // A
                            }
                        }

                        currentBitmap.AddDirtyRect(new Int32Rect(0, 0, currentBitmap.PixelWidth, currentBitmap.PixelHeight));
                    }
                    finally
                    {
                        currentBitmap.Unlock();
                    }

                    Console.WriteLine($"Updated to color index: {colorIndex}");
                }

                colorIndex = (colorIndex + 1) % colors.Length;
            };
            timer.Start();
        }
    }

    public class FFmpegException : Exception
    {
        public FFmpegException(string message, int errorCode) : base($"{message} (error code: {errorCode})") { }
    }
}