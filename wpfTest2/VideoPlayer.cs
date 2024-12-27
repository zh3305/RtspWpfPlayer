using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

public class VideoPlayer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private WriteableBitmap _currentBitmap;
    private readonly object _bitmapLock = new();
    private bool _disposed;
    private GCHandle _getFormatCallbackHandle;
    private AVCodecContext_get_format _getFormatCallback;
    private readonly Image _targetImage;
    private readonly string _url;
    private Task _playbackTask;
    private readonly ILogger<VideoPlayer> _logger;
    private volatile bool _isPlaying;
    private readonly SemaphoreSlim _playbackSemaphore = new(1, 1);
    private const int StopTimeoutMs = 3000; // 3秒超时

    public bool IsPlaying => _isPlaying;
    public string Url => _url;
    public event EventHandler<Exception> OnError;
    public event EventHandler<string> OnStatusChanged;

    public VideoPlayer(Image targetImage, string url, ILogger<VideoPlayer> logger)
    {
        _targetImage = targetImage ?? throw new ArgumentNullException(nameof(targetImage));
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        InitializeVideoDisplay();
    }

    public async Task StartAsync()
    {
        if (_isPlaying)
        {
            _logger.LogWarning("Player is already running");
            return;
        }

        await _playbackSemaphore.WaitAsync();
        try
        {
            _isPlaying = true;
            _playbackTask = Task.Run(() => PlaybackLoop(_cts.Token));
            OnStatusChanged?.Invoke(this, "Started");
        }
        finally
        {
            _playbackSemaphore.Release();
        }
    }

    public void Start()
    {
        StartAsync().Wait();
    }

    public async Task StopAsync()
    {
        if (!_isPlaying) return;

        _logger.LogInformation("Stopping player for URL: {Url}", _url);
        
        try
        {
            _isPlaying = false;
            _cts.Cancel();
            
            if (_playbackTask != null)
            {
                // 设置更短的超时时间
                using var timeoutCts = new CancellationTokenSource(1000); // 1秒超时
                try
                {
                    await _playbackTask.WaitAsync(timeoutCts.Token);
                    _logger.LogInformation("Player stopped normally");
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Stop operation timed out, forcing stop");
                }
                finally
                {
                    _playbackTask = null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during stop operation");
        }

        OnStatusChanged?.Invoke(this, "Stopped");
    }

    public void Stop()
    {
        StopAsync().Wait();
    }

    private unsafe void PlaybackLoop(CancellationToken token)
    {
        AVFormatContext* formatContext = null;
        AVCodecContext* codecContext = null;
        AVFrame* frame = null;
        AVPacket* packet = null;

        try
        {
            _logger.LogInformation("Starting playback for URL: {Url}", _url);

            // 设置较短的网络超时
            var options = ffmpeg.avformat_alloc_context();
            var timeout = 1000000; // 1秒 (以微秒为单位)
            ffmpeg.av_dict_set(&options->metadata, "stimeout", timeout.ToString(), 0);
            ffmpeg.av_dict_set(&options->metadata, "rtsp_transport", "tcp", 0);

            int result = ffmpeg.avformat_open_input(&formatContext, _url, null, null);
            if (result < 0)
            {
                throw new FFmpegException("Failed to open input", result);
            }

            if (ffmpeg.avformat_find_stream_info(formatContext, null) < 0)
            {
                throw new FFmpegException("Failed to find stream info", -1);
            }

            var videoStream = FindVideoStream(formatContext);
            var codec = SetupCodec(formatContext, videoStream, out codecContext);
            
            _logger.LogInformation("Using codec: {CodecName}", Marshal.PtrToStringAnsi((IntPtr)codec->name));

            ConfigureHardwareAcceleration(codecContext);
            
            if (ffmpeg.avcodec_open2(codecContext, codec, null) < 0)
            {
                throw new InvalidOperationException("Failed to open codec");
            }

            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();

            while (!token.IsCancellationRequested && _isPlaying)
            {
                // 添加超时检查
                if (token.IsCancellationRequested)
                {
                    _logger.LogInformation("Cancellation requested, breaking playback loop");
                    break;
                }

                // 使用较短的读取超时
                result = ffmpeg.av_read_frame(formatContext, packet);
                if (result < 0)
                {
                    if (result == ffmpeg.AVERROR_EOF || result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        _logger.LogInformation("End of stream or timeout reached");
                        break;
                    }
                    throw new FFmpegException("Error reading frame", result);
                }

                if (packet->stream_index == videoStream)
                {
                    ProcessVideoPacket(codecContext, frame, packet);
                }

                ffmpeg.av_packet_unref(packet);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error in playback loop");
                OnError?.Invoke(this, ex);
            }
        }
        finally
        {
            _logger.LogInformation("Cleaning up resources");
            CleanupResources(ref frame, ref packet, ref codecContext, ref formatContext);
        }
    }

    private unsafe void ProcessVideoPacket(AVCodecContext* codecContext, AVFrame* frame, AVPacket* packet)
    {
        try
        {
            int ret = ffmpeg.avcodec_send_packet(codecContext, packet);
            if (ret < 0)
            {
                _logger.LogError("Error sending packet for decoding: {Error}", ret);
                return;
            }

            while (ret >= 0)
            {
                ret = ffmpeg.avcodec_receive_frame(codecContext, frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    break;
                if (ret < 0)
                {
                    _logger.LogError("Error receiving frame: {Error}", ret);
                    break;
                }

                _logger.LogTrace("Decoded frame: {Width}x{Height}, format: {Format}, pts: {Pts}",
                    frame->width, frame->height, frame->format, frame->pts);

                RenderFrame(frame);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing video packet");
            OnError?.Invoke(this, ex);
        }
    }

    private unsafe void RenderFrame(AVFrame* frame)
    {
        if (frame == null) return;

        try
        {
            AVFrame* swFrame = ffmpeg.av_frame_alloc();
            try
            {
                swFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
                swFrame->width = frame->width;
                swFrame->height = frame->height;

                int ret = ffmpeg.av_frame_get_buffer(swFrame, 32);
                if (ret < 0)
                {
                    _logger.LogError("Failed to allocate frame buffer: {Error}", ret);
                    return;
                }

                ret = ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0);
                if (ret < 0)
                {
                    _logger.LogError("Failed to transfer hardware frame data: {Error}", ret);
                    return;
                }

                UpdateBitmap(swFrame);
            }
            finally
            {
                var temp = swFrame;
                ffmpeg.av_frame_free(&temp);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rendering frame");
            OnError?.Invoke(this, ex);
        }
    }

    private unsafe void UpdateBitmap(AVFrame* frame)
    {
        _targetImage.Dispatcher.Invoke(() =>
        {
            try
            {
                lock (_bitmapLock)
                {
                    if (_currentBitmap == null || 
                        _currentBitmap.PixelWidth != frame->width || 
                        _currentBitmap.PixelHeight != frame->height)
                    {
                        _currentBitmap = new WriteableBitmap(
                            frame->width,
                            frame->height,
                            96,
                            96,
                            PixelFormats.Bgra32,
                            null);
                        _targetImage.Source = _currentBitmap;
                    }

                    _currentBitmap.Lock();
                    try
                    {
                        ConvertAndCopyFrame(frame);
                    }
                    finally
                    {
                        _currentBitmap.Unlock();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating bitmap");
                OnError?.Invoke(this, ex);
            }
        });
    }

    private unsafe void ConvertAndCopyFrame(AVFrame* frame)
    {
        var swsContext = ffmpeg.sws_getContext(
            frame->width, frame->height, AVPixelFormat.AV_PIX_FMT_NV12,
            frame->width, frame->height, AVPixelFormat.AV_PIX_FMT_BGRA,
            ffmpeg.SWS_BILINEAR, null, null, null);

        if (swsContext == null)
        {
            _logger.LogError("Failed to create SwsContext");
            return;
        }

        try
        {
            byte_ptrArray4 dstData = new byte_ptrArray4();
            int_array4 dstLinesize = new int_array4();

            dstData[0] = (byte*)_currentBitmap.BackBuffer;
            dstLinesize[0] = _currentBitmap.BackBufferStride;

            ffmpeg.sws_scale(swsContext,
                frame->data, frame->linesize, 0, frame->height,
                dstData, dstLinesize);

            _currentBitmap.AddDirtyRect(new Int32Rect(0, 0, frame->width, frame->height));
        }
        finally
        {
            ffmpeg.sws_freeContext(swsContext);
        }
    }

    private void InitializeVideoDisplay()
    {
        try
        {
            _currentBitmap = new WriteableBitmap(16, 16, 96, 96, PixelFormats.Bgra32, null);
            _targetImage.Source = _currentBitmap;
            _logger.LogInformation("Video display initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize video display");
            OnError?.Invoke(this, ex);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            StopAsync().Wait();
            if (_getFormatCallbackHandle.IsAllocated)
            {
                _getFormatCallbackHandle.Free();
            }
            _cts.Dispose();
            _playbackSemaphore.Dispose();
            _disposed = true;
            _logger.LogInformation("Resources disposed");
            GC.SuppressFinalize(this);
        }
    }

    private unsafe void CleanupResources(
        ref AVFrame* frame,
        ref AVPacket* packet,
        ref AVCodecContext* codecContext,
        ref AVFormatContext* formatContext)
    {
        try
        {
            if (frame != null)
            {
                var tempFrame = frame;
                ffmpeg.av_frame_free(&tempFrame);
                frame = null;
            }

            if (packet != null)
            {
                var tempPacket = packet;
                ffmpeg.av_packet_free(&tempPacket);
                packet = null;
            }

            if (codecContext != null)
            {
                var tempCodecContext = codecContext;
                ffmpeg.avcodec_free_context(&tempCodecContext);
                codecContext = null;
            }

            if (formatContext != null)
            {
                var tempFormatContext = formatContext;
                ffmpeg.avformat_close_input(&tempFormatContext);
                formatContext = null;
            }

            _logger.LogInformation("Resources cleaned up successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during resource cleanup");
        }
    }

    private unsafe int FindVideoStream(AVFormatContext* formatContext)
    {
        for (int i = 0; i < formatContext->nb_streams; i++)
        {
            if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                _logger.LogInformation("Found video stream at index: {Index}", i);
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

        _logger.LogInformation("Codec setup completed: {CodecName}", Marshal.PtrToStringAnsi((IntPtr)codec->name));
        return codec;
    }

    private unsafe void ConfigureHardwareAcceleration(AVCodecContext* codecContext)
    {
        try
        {
            _getFormatCallback = (ctx, fmt) =>
            {
                _logger.LogInformation("Selecting pixel format for hardware acceleration");
                return AVPixelFormat.AV_PIX_FMT_DXVA2_VLD;
            };

            _getFormatCallbackHandle = GCHandle.Alloc(_getFormatCallback);
            codecContext->get_format = new AVCodecContext_get_format_func 
            { 
                Pointer = Marshal.GetFunctionPointerForDelegate(_getFormatCallback)
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

            _logger.LogInformation("Hardware acceleration configured successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to configure hardware acceleration");
            throw;
        }
    }

    private unsafe int InterruptCallback(void* opaque)
    {
        return _cts.Token.IsCancellationRequested ? 1 : 0;
    }
}