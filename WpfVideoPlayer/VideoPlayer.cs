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
using WpfVideoPlayer.Interfaces;

public class VideoPlayer : IVideoPlayer
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
    private const int MAX_RECONNECT_ATTEMPTS = 3;
    private const int RECONNECT_DELAY_MS = 2000;

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
        int reconnectAttempts = 0;
        bool needReconnect = true;

        while (needReconnect && reconnectAttempts < MAX_RECONNECT_ATTEMPTS && !token.IsCancellationRequested)
        {
            if (reconnectAttempts > 0)
            {
                _logger.LogInformation("Attempting to reconnect ({Attempt}/{MaxAttempts})...", 
                    reconnectAttempts + 1, MAX_RECONNECT_ATTEMPTS);
                Thread.Sleep(RECONNECT_DELAY_MS); // 使用 Thread.Sleep 替代 Task.Delay
            }

            AVFormatContext* formatContext = null;
            AVCodecContext* codecContext = null;
            AVFrame* frame = null;
            AVPacket* packet = null;

            try
            {
                _logger.LogInformation("Starting playback for URL: {Url}", _url);

                // 设置网络选项
                AVDictionary* dict = null;
                ffmpeg.av_dict_set(&dict, "stimeout", "2000000", 0);  // 2秒超时
                ffmpeg.av_dict_set(&dict, "rtsp_transport", "tcp", 0);
                ffmpeg.av_dict_set(&dict, "buffer_size", "1024000", 0); // 设置缓冲区大小
                ffmpeg.av_dict_set(&dict, "max_delay", "500000", 0);   // 最大延迟500ms
                ffmpeg.av_dict_set(&dict, "reconnect", "1", 0);        // 启用重连
                ffmpeg.av_dict_set(&dict, "reconnect_at_eof", "1", 0); // 文件结束时重连
                ffmpeg.av_dict_set(&dict, "reconnect_streamed", "1", 0); // 流媒体重连
                ffmpeg.av_dict_set(&dict, "reconnect_delay_max", "2", 0); // 最大重连延迟

                int result = ffmpeg.avformat_open_input(&formatContext, _url, null, &dict);
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
                
                ConfigureHardwareAcceleration(codecContext);
                
                if (ffmpeg.avcodec_open2(codecContext, codec, null) < 0)
                {
                    throw new InvalidOperationException("Failed to open codec");
                }

                frame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();

                int errorCount = 0;
                const int MAX_ERRORS = 5;
                DateTime lastErrorTime = DateTime.Now;

                while (!token.IsCancellationRequested && _isPlaying)
                {
                    if (token.IsCancellationRequested) break;

                    result = ffmpeg.av_read_frame(formatContext, packet);
                    if (result < 0)
                    {
                        if (result == ffmpeg.AVERROR_EOF)
                        {
                            _logger.LogInformation("End of stream reached");
                            break;
                        }
                        
                        if (DateTime.Now - lastErrorTime > TimeSpan.FromSeconds(5))
                        {
                            errorCount = 0;
                            lastErrorTime = DateTime.Now;
                        }

                        errorCount++;
                        if (errorCount > MAX_ERRORS)
                        {
                            _logger.LogWarning("Too many errors, attempting reconnect");
                            break;
                        }

                        Thread.Sleep(100); // 使用 Thread.Sleep 替代 Task.Delay
                        continue;
                    }

                    if (packet->stream_index == videoStream)
                    {
                        ProcessVideoPacket(codecContext, frame, packet);
                        errorCount = 0; // 重置错误计数
                    }

                    ffmpeg.av_packet_unref(packet);
                }

                // 如果是正常退出，不需要重连
                if (token.IsCancellationRequested || !_isPlaying)
                {
                    needReconnect = false;
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Error in playback loop");
                    OnError?.Invoke(this, ex);
                    reconnectAttempts++;
                }
                else
                {
                    needReconnect = false;
                }
            }
            finally
            {
                CleanupResources(ref frame, ref packet, ref codecContext, ref formatContext);
            }
        }

        if (reconnectAttempts >= MAX_RECONNECT_ATTEMPTS)
        {
            _logger.LogError("Max reconnection attempts reached");
            OnError?.Invoke(this, new Exception("Failed to reconnect after maximum attempts"));
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

                // 如果是硬件帧，需要转换到系统内存
                if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_DXVA2_VLD)
                {
                    var swFrame = ffmpeg.av_frame_alloc();
                    try
                    {
                        ret = ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0);
                        if (ret < 0)
                        {
                            _logger.LogError("Error transferring hw frame: {Error}", ret);
                            continue;
                        }
                        RenderFrame(swFrame);
                    }
                    finally
                    {
                        var temp = swFrame;
                        ffmpeg.av_frame_free(&temp);
                    }
                }
                else
                {
                    RenderFrame(frame);
                }
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
            // 检查帧数据是否有效
            if (frame->data[0] == null)
            {
                _logger.LogWarning("Invalid frame data");
                return;
            }

            // 确定源格式
            var srcFormat = (AVPixelFormat)frame->format;
            if (srcFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            {
                _logger.LogWarning("Invalid pixel format");
                return;
            }

            var swFrame = ffmpeg.av_frame_alloc();
            try
            {
                // 设置目标帧参数
                swFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                swFrame->width = frame->width;
                swFrame->height = frame->height;

                int ret = ffmpeg.av_frame_get_buffer(swFrame, 32);
                if (ret < 0)
                {
                    _logger.LogError("Failed to allocate frame buffer: {Error}", ret);
                    return;
                }

                // 确保帧数据对齐
                ret = ffmpeg.av_frame_make_writable(swFrame);
                if (ret < 0)
                {
                    _logger.LogError("Failed to make frame writable: {Error}", ret);
                    return;
                }

                var swsContext = ffmpeg.sws_getContext(
                    frame->width, frame->height, srcFormat,
                    frame->width, frame->height, AVPixelFormat.AV_PIX_FMT_BGRA,
                    ffmpeg.SWS_BILINEAR | ffmpeg.SWS_ACCURATE_RND, null, null, null);

                if (swsContext == null)
                {
                    _logger.LogError("Failed to create SwsContext");
                    return;
                }

                try
                {
                    // 检查源和目标数据指针
                    if (frame->data[0] != null && swFrame->data[0] != null)
                    {
                        ret = ffmpeg.sws_scale(swsContext,
                            frame->data, frame->linesize, 0, frame->height,
                            swFrame->data, swFrame->linesize);

                        if (ret < 0)
                        {
                            _logger.LogError("Failed to convert frame: {Error}", ret);
                            return;
                        }

                        UpdateBitmap(swFrame);
                    }
                    else
                    {
                        _logger.LogWarning("Invalid data pointers");
                    }
                }
                finally
                {
                    ffmpeg.sws_freeContext(swsContext);
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
            _logger.LogError(ex, "Error rendering frame");
            OnError?.Invoke(this, ex);
        }
    }

    private unsafe void UpdateBitmap(AVFrame* frame)
    {
        if (frame == null || frame->data[0] == null) return;

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
                        var stride = frame->linesize[0];
                        var dataPtr = frame->data[0];
                        var bufferSize = stride * frame->height;

                        unsafe
                        {
                            Buffer.MemoryCopy(
                                dataPtr,
                                (void*)_currentBitmap.BackBuffer,
                                bufferSize,
                                bufferSize);
                        }

                        _currentBitmap.AddDirtyRect(
                            new Int32Rect(0, 0, frame->width, frame->height));
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
        // 优先查找硬件解码器
        var codec = ffmpeg.avcodec_find_decoder_by_name($"{ffmpeg.avcodec_get_name(codecParams->codec_id)}_dxva2");
        if (codec == null)
        {
            // 回退到软解码
            codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
        }
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

        // 尝试启用硬件加速
        if (IsHardwareAccelerationSupported(codecContext))
        {
            codecContext->hw_device_ctx = null;
            if (TryCreateHardwareDeviceContext(codecContext))
            {
                _logger.LogInformation("Hardware acceleration enabled");
                codecContext->get_format = null; // 使用硬件加速时不需要自定义格式选择
            }
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
                var formats = new List<AVPixelFormat>();
                var p = fmt;
                while (*p != AVPixelFormat.AV_PIX_FMT_NONE)
                {
                    formats.Add(*p);
                    p++;
                }

                // 首先尝试使用原始格式
                var originalFormat = codecContext->pix_fmt;
                if (formats.Contains(originalFormat))
                {
                    _logger.LogInformation("Using original format: {Format}", originalFormat);
                    return originalFormat;
                }

                // 然后尝试常用格式
                var preferredFormats = new[]
                {
                    AVPixelFormat.AV_PIX_FMT_YUV420P,
                    AVPixelFormat.AV_PIX_FMT_NV12,
                    AVPixelFormat.AV_PIX_FMT_YUV444P,
                    AVPixelFormat.AV_PIX_FMT_BGRA
                };

                foreach (var format in preferredFormats)
                {
                    if (formats.Contains(format))
                    {
                        _logger.LogInformation("Using preferred format: {Format}", format);
                        return format;
                    }
                }

                // 如果都不支持，使用第一个可用格式
                _logger.LogInformation("Using first available format: {Format}", formats[0]);
                return formats[0];
            };

            _getFormatCallbackHandle = GCHandle.Alloc(_getFormatCallback, GCHandleType.Normal);
            codecContext->get_format = new AVCodecContext_get_format_func(){Pointer = Marshal.GetFunctionPointerForDelegate(_getFormatCallback)};

            _logger.LogInformation("Pixel format configuration completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to configure pixel format");
        }
    }

    private unsafe bool IsHardwareAccelerationSupported(AVCodecContext* codecContext)
    {
        try
        {
            var deviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2;
            var config = ffmpeg.avcodec_get_hw_config(codecContext->codec, 0);
            
            if (config == null)
            {
                _logger.LogInformation("No hardware config available");
                return false;
            }

            // 简化硬件加速检查
            if (config->device_type != deviceType)
            {
                _logger.LogInformation("DXVA2 not supported for this codec");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking hardware acceleration support");
            return false;
        }
    }

    private unsafe bool TryCreateHardwareDeviceContext(AVCodecContext* codecContext)
    {
        try
        {
            int result = ffmpeg.av_hwdevice_ctx_create(
                &codecContext->hw_device_ctx,
                AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,
                null,
                null,
                0);

            if (result < 0)
            {
                _logger.LogWarning("Failed to create hardware device context: {Error}", result);
                return false;
            }

            // 创建硬件帧上下文
            var hwFramesContext = ffmpeg.av_hwframe_ctx_alloc(codecContext->hw_device_ctx);
            if (hwFramesContext == null)
            {
                _logger.LogWarning("Failed to create hardware frame context");
                return false;
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
                _logger.LogWarning("Failed to initialize hardware frame context");
                return false;
            }

            codecContext->hw_frames_ctx = ffmpeg.av_buffer_ref(hwFramesContext);
            ffmpeg.av_buffer_unref(&hwFramesContext);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating hardware device context");
            return false;
        }
    }

    private unsafe int InterruptCallback(void* opaque)
    {
        return _cts.Token.IsCancellationRequested ? 1 : 0;
    }
}