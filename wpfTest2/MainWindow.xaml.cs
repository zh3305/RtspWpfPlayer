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
        private readonly CancellationTokenSource cts = new();
        private WriteableBitmap currentBitmap;
        private readonly object bitmapLock = new();
        private bool disposed;
        private GCHandle getFormatCallbackHandle;
        private AVCodecContext_get_format getFormatCallback;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("Application starting...");
            InitializeVideoDisplay();
            Task.Run(() => PlaybackLoop(cts.Token));
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            Console.WriteLine("Application closing...");
            cts.Cancel();
            Dispose();
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
                
                if (ffmpeg.avformat_find_stream_info(formatContext, null) < 0)
                {
                    throw new FFmpegException("Failed to find stream info", -1);
                }

                var videoStream = FindVideoStream(formatContext);
                Console.WriteLine($"Found video stream at index: {videoStream}");

                var codec = SetupCodec(formatContext, videoStream, out codecContext);
                Console.WriteLine($"Using codec: {Marshal.PtrToStringAnsi((IntPtr)codec->name)}");

                ConfigureHardwareAcceleration(codecContext);
                Console.WriteLine("Hardware acceleration configured");

                if (ffmpeg.avcodec_open2(codecContext, codec, null) < 0)
                {
                    throw new InvalidOperationException("Failed to open codec");
                }
                Console.WriteLine("Codec opened successfully");

                frame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();

                Console.WriteLine("Starting frame reading loop...");
                while (!token.IsCancellationRequested)
                {
                    if (ffmpeg.av_read_frame(formatContext, packet) < 0) break;

                    if (packet->stream_index == videoStream)
                    {
                        Console.WriteLine($"Processing video packet, size: {packet->size}, pts: {packet->pts}");
                        ProcessVideoPacket(codecContext, frame, packet);
                    }

                    ffmpeg.av_packet_unref(packet);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error in playback loop: {ex}");
            }
            finally
            {
                Console.WriteLine("Cleaning up resources...");
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

        private unsafe void ConfigureHardwareAcceleration(AVCodecContext* codecContext)
        {
            try
            {
                getFormatCallback = (ctx, fmt) =>
                {
                    Console.WriteLine("Selecting pixel format for hardware acceleration");
                    return AVPixelFormat.AV_PIX_FMT_DXVA2_VLD;
                };

                getFormatCallbackHandle = GCHandle.Alloc(getFormatCallback);
                codecContext->get_format = new AVCodecContext_get_format_func 
                { 
                    Pointer = Marshal.GetFunctionPointerForDelegate(getFormatCallback)
                };

                // 设置线程数为1，避免多线程问题
                codecContext->thread_count = 1;
                codecContext->thread_type = ffmpeg.FF_THREAD_SLICE;

                // 创建硬件设备上下文
                int result = ffmpeg.av_hwdevice_ctx_create(
                    &codecContext->hw_device_ctx,
                    AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,
                    null,
                    null,
                    0);

                if (result < 0)
                {
                    throw new FFmpegException("Failed to create hardware device context", result);
                }

                // 创建硬件帧上下文
                var hwFramesContext = ffmpeg.av_hwframe_ctx_alloc(codecContext->hw_device_ctx);
                if (hwFramesContext == null)
                {
                    throw new Exception("Failed to create hardware frame context");
                }

                var framesContext = (AVHWFramesContext*)hwFramesContext->data;
                framesContext->format = AVPixelFormat.AV_PIX_FMT_DXVA2_VLD;
                framesContext->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
                framesContext->width = codecContext->width;
                framesContext->height = codecContext->height;
                framesContext->initial_pool_size = 20;

                result = ffmpeg.av_hwframe_ctx_init(hwFramesContext);
                if (result < 0)
                {
                    ffmpeg.av_buffer_unref(&hwFramesContext);
                    throw new FFmpegException("Failed to initialize hardware frame context", result);
                }

                codecContext->hw_frames_ctx = ffmpeg.av_buffer_ref(hwFramesContext);
                ffmpeg.av_buffer_unref(&hwFramesContext);

                Console.WriteLine("Hardware acceleration configured successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to configure hardware acceleration: {ex.Message}");
                throw;
            }
        }

        private unsafe void ProcessVideoPacket(AVCodecContext* codecContext, AVFrame* frame, AVPacket* packet)
        {
            try
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
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing video packet: {ex.Message}");
            }
        }

        private unsafe void RenderFrame(AVFrame* frame)
        {
            try
            {
                if (frame == null)
                {
                    Console.WriteLine("Null frame received");
                    return;
                }

                Console.WriteLine($"Rendering frame: format={frame->format}, size={frame->width}x{frame->height}, data[3]={(long)frame->data[3]:X}");

                // 获取软件格式的帧
                AVFrame* swFrame = ffmpeg.av_frame_alloc();
                try
                {
                    swFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;  // 使用 NV12 格式
                    swFrame->width = frame->width;
                    swFrame->height = frame->height;

                    // 分配软件帧的缓冲区
                    int ret = ffmpeg.av_frame_get_buffer(swFrame, 32);
                    if (ret < 0)
                    {
                        Console.WriteLine($"Failed to allocate frame buffer: {ret}");
                        return;
                    }

                    // 从硬件帧获取数据
                    ret = ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0);
                    if (ret < 0)
                    {
                        Console.WriteLine($"Failed to transfer hardware frame data: {ret}");
                        return;
                    }

                    // 验证软件帧数据
                    if (swFrame->data[0] == null || swFrame->data[1] == null)
                    {
                        Console.WriteLine("Software frame data is null after transfer");
                        return;
                    }

                    Console.WriteLine($"Frame transferred to software format: linesize=[{swFrame->linesize[0]}, {swFrame->linesize[1]}]");

                    // 转换 NV12 到 YUV420P
                    AVFrame* yuvFrame = ffmpeg.av_frame_alloc();
                    try
                    {
                        yuvFrame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                        yuvFrame->width = frame->width;
                        yuvFrame->height = frame->height;

                        ret = ffmpeg.av_frame_get_buffer(yuvFrame, 32);
                        if (ret < 0)
                        {
                            Console.WriteLine($"Failed to allocate YUV frame buffer: {ret}");
                            return;
                        }

                        // 创建 SwsContext 进行格式转换
                        var swsContext = ffmpeg.sws_getContext(
                            frame->width, frame->height, AVPixelFormat.AV_PIX_FMT_NV12,
                            frame->width, frame->height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                            0, null, null, null);

                        if (swsContext == null)
                        {
                            Console.WriteLine("Failed to create SwsContext");
                            return;
                        }

                        try
                        {
                            ffmpeg.sws_scale(swsContext,
                                swFrame->data, swFrame->linesize, 0, frame->height,
                                yuvFrame->data, yuvFrame->linesize);

                            // 渲染 YUV420P 帧
                            RenderYUVFrame(yuvFrame);
                        }
                        finally
                        {
                            ffmpeg.sws_freeContext(swsContext);
                        }
                    }
                    finally
                    {
                        var temp = yuvFrame;
                        ffmpeg.av_frame_free(&temp);
                    }
                }
                finally
                {
                    var temp = swFrame;
                    ffmpeg.av_frame_free(&temp);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in RenderFrame: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private unsafe void RenderYUVFrame(AVFrame* frame)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    lock (bitmapLock)
                    {
                        if (currentBitmap == null || 
                            currentBitmap.PixelWidth != frame->width || 
                            currentBitmap.PixelHeight != frame->height)
                        {
                            Console.WriteLine($"Creating new bitmap: {frame->width}x{frame->height}");
                            currentBitmap = new WriteableBitmap(
                                frame->width,
                                frame->height,
                                96,
                                96,
                                PixelFormats.Bgra32,
                                null);
                            videoImage.Source = currentBitmap;
                        }

                        currentBitmap.Lock();
                        try
                        {
                            var backBuffer = currentBitmap.BackBuffer;
                            var stride = currentBitmap.BackBufferStride;

                            YUV420PtoBGRA(
                                frame->data[0], frame->linesize[0],
                                frame->data[1], frame->linesize[1],
                                frame->data[2], frame->linesize[2],
                                (byte*)backBuffer,
                                stride,
                                frame->width,
                                frame->height);

                            currentBitmap.AddDirtyRect(new Int32Rect(0, 0, frame->width, frame->height));
                        }
                        finally
                        {
                            currentBitmap.Unlock();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error updating bitmap: {ex.Message}\n{ex.StackTrace}");
                }
            });
        }

        private unsafe void YUV420PtoBGRA(
            byte* y, int yStride,
            byte* u, int uStride,
            byte* v, int vStride,
            byte* bgra, int bgraStride,
            int width, int height)
        {
            try
            {
                Console.WriteLine($"Converting YUV420P to BGRA: {width}x{height}");
                Console.WriteLine($"Strides - Y: {yStride}, U: {uStride}, V: {vStride}, BGRA: {bgraStride}");

                if (y == null || u == null || v == null || bgra == null)
                {
                    throw new ArgumentNullException("One or more input pointers is null");
                }

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

                        Y = (Y - 16) * 298;

                        int B = (Y + 516 * U + 128) >> 8;
                        int G = (Y - 100 * U - 208 * V + 128) >> 8;
                        int R = (Y + 409 * V + 128) >> 8;

                        B = B < 0 ? 0 : B > 255 ? 255 : B;
                        G = G < 0 ? 0 : G > 255 ? 255 : G;
                        R = R < 0 ? 0 : R > 255 ? 255 : R;

                        bgraLine[i * 4 + 0] = (byte)B;
                        bgraLine[i * 4 + 1] = (byte)G;
                        bgraLine[i * 4 + 2] = (byte)R;
                        bgraLine[i * 4 + 3] = 255;
                    }
                }
                Console.WriteLine("YUV to BGRA conversion completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in YUV420PtoBGRA: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        private void InitializeVideoDisplay()
        {
            try
            {
                currentBitmap = new WriteableBitmap(
                    16,
                    16,
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

        public void Dispose()
        {
            if (!disposed)
            {
                if (getFormatCallbackHandle.IsAllocated)
                {
                    getFormatCallbackHandle.Free();
                }
                cts.Dispose();
                disposed = true;
                Console.WriteLine("Resources disposed");
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
    }

    public class FFmpegException : Exception
    {
        public FFmpegException(string message, int errorCode) 
            : base($"{message} (error code: {errorCode})") { }
    }
}