//采用DXVA2解码+D3D11渲染 的实现。
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device = SharpDX.Direct3D11.Device;
using Resource = SharpDX.Direct3D11.Resource;
using SharpDX.Direct3D9;
using D3D11Device = SharpDX.Direct3D11.Device;
using D3D11Resource = SharpDX.Direct3D11.Resource;
using D3D11Texture2D = SharpDX.Direct3D11.Texture2D;
using D3D9 = SharpDX.Direct3D9;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;
using WpfVideoPlayer.Interfaces;

/// <summary>
/// 采用DXVA2解码+D3D11渲染 的实现。
/// </summary>
public class DX11VideoPlayer : IVideoPlayer
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Image _targetImage;
    private readonly string _url;
    private Task _playbackTask;
    private readonly ILogger<DX11VideoPlayer> _logger;
    private volatile bool _isPlaying;
    private readonly SemaphoreSlim _playbackSemaphore = new(1, 1);
    private bool _disposed;

    // D3D11 相关字段
    private D3D11.Device _device;
    private D3D11.DeviceContext _context;
    private D3D11.Texture2D _renderTexture;
    private D3D11.ShaderResourceView _renderShaderView;
    private D3D11ImageSource _d3dImage;
    private DXGI.Factory1 _factory;

    // D3D9 相关字段
    private D3D9.Direct3D _d3d9;
    private D3D9.Device _d3d9Device;
    private D3D9.Surface _d3d9Surface;

    // 添加回调相关字段
    private unsafe delegate AVPixelFormat GetHWFormatDelegate(AVCodecContext* ctx, AVPixelFormat* pix_fmts);
    private GetHWFormatDelegate _getFormatCallback;
    private GCHandle _getFormatCallbackHandle;

    public bool IsPlaying => _isPlaying;
    public string Url => _url;
    public event EventHandler<Exception> OnError;
    public event EventHandler<string> OnStatusChanged;

    public DX11VideoPlayer(Image targetImage, string url, ILogger<DX11VideoPlayer> logger)
    {
        _targetImage = targetImage ?? throw new ArgumentNullException(nameof(targetImage));
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        InitializeD3D11();
    }

    private unsafe Texture2D GetD3D11Frame(AVFrame* frame)
    {
        try
        {
            if (frame->format != (int)AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                _logger.LogWarning("Frame format is not D3D11");
                return null;
            }

            var hwFramesContext = (AVHWFramesContext*)frame->hw_frames_ctx->data;
            if (hwFramesContext == null)
            {
                _logger.LogError("No hardware frames context");
                return null;
            }

            // 使用 SharpDX 的原生方式获取纹理
            IntPtr nativeTexture = (IntPtr)frame->data[0];
            if (nativeTexture == IntPtr.Zero)
            {
                _logger.LogError("Failed to get native texture");
                return null;
            }

            return new Texture2D(nativeTexture);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting D3D11 frame");
            return null;
        }
    }

    private void InitializeD3D11()
    {
        try
        {
            // 初始化 D3D9
            _d3d9 = new D3D9.Direct3D();
            var presentParams = new D3D9.PresentParameters
            {
                Windowed = true,
                SwapEffect = D3D9.SwapEffect.Discard,
                DeviceWindowHandle = GetDesktopWindow(),
                BackBufferFormat = D3D9.Format.A8R8G8B8,
                BackBufferWidth = 1920,
                BackBufferHeight = 1080,
                BackBufferCount = 1,
                PresentationInterval = D3D9.PresentInterval.Default
            };

            _d3d9Device = new D3D9.Device(
                _d3d9,
                0,
                D3D9.DeviceType.Hardware,
                IntPtr.Zero,
                D3D9.CreateFlags.HardwareVertexProcessing | D3D9.CreateFlags.Multithreaded,
                presentParams);

            // 初始化 D3D11
            _factory = new DXGI.Factory1();
            var adapter = _factory.GetAdapter1(0);
            _device = new D3D11.Device(adapter, D3D11.DeviceCreationFlags.BgraSupport | D3D11.DeviceCreationFlags.VideoSupport);
            _context = _device.ImmediateContext;

            // 创建共享纹理
            var texDesc = new D3D11.Texture2DDescription
            {
                Width = 1920,
                Height = 1080,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Default,
                BindFlags = D3D11.BindFlags.ShaderResource | D3D11.BindFlags.RenderTarget,
                CpuAccessFlags = D3D11.CpuAccessFlags.None,
                OptionFlags = D3D11.ResourceOptionFlags.Shared
            };

            _renderTexture = new D3D11.Texture2D(_device, texDesc);
            _renderShaderView = new D3D11.ShaderResourceView(_device, _renderTexture);

            // 创建 D3D9 表面 - 修改这部分
            _d3d9Surface = D3D9.Surface.CreateRenderTarget(
                _d3d9Device,
                1920, 1080,
                D3D9.Format.A8R8G8B8,
                D3D9.MultisampleType.None,
                0,
                true);  // 设置为可锁定

            // 创建 D3DImage
            _d3dImage = new D3D11ImageSource();
            _targetImage.Source = _d3dImage;

            // 等待 UI 线程完成初始化
            _targetImage.Dispatcher.Invoke(() =>
            {
                try
                {
                    _d3dImage.SetBackBuffer(_d3d9Surface.NativePointer);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error setting back buffer");
                    throw;
                }
            });

            _logger.LogInformation("D3D11 and D3D9 initialization completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize D3D11/D3D9");
            throw;
        }
    }

    private unsafe AVBufferRef* CreateHWFramesContext(AVCodecContext* codecContext)
    {
        var hwFramesRef = ffmpeg.av_hwframe_ctx_alloc(codecContext->hw_device_ctx);
        if (hwFramesRef == null) return null;

        var hwFramesCtx = (AVHWFramesContext*)hwFramesRef->data;
        hwFramesCtx->format = AVPixelFormat.AV_PIX_FMT_DXVA2_VLD;
        hwFramesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        hwFramesCtx->width = codecContext->width;
        hwFramesCtx->height = codecContext->height;
        hwFramesCtx->initial_pool_size = 20;

        var result = ffmpeg.av_hwframe_ctx_init(hwFramesRef);
        if (result < 0)
        {
            _logger.LogError("Failed to initialize hardware frames context: {Error}", result);
            ffmpeg.av_buffer_unref(&hwFramesRef);
            return null;
        }

        return hwFramesRef;
    }

    private unsafe void RenderD3D11Frame(AVFrame* frame)
    {
        if (frame == null || frame->data[0] == null)
        {
            _logger.LogWarning("Invalid frame data");
            return;
        }

        try
        {
            _logger.LogDebug("Starting frame rendering: format={Format}, width={Width}, height={Height}, linesize={Linesize}",
                frame->format, frame->width, frame->height, frame->linesize[0]);

            // 创建转换上下文
            var swsContext = ffmpeg.sws_getContext(
                frame->width, frame->height, (AVPixelFormat)frame->format,
                frame->width, frame->height, AVPixelFormat.AV_PIX_FMT_BGRA,
                ffmpeg.SWS_BILINEAR, null, null, null);

            if (swsContext == null)
            {
                _logger.LogError("Failed to create scaling context");
                return;
            }

            try
            {
                // 创建目标帧
                var rgbaFrame = ffmpeg.av_frame_alloc();
                try
                {
                    rgbaFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                    rgbaFrame->width = frame->width;
                    rgbaFrame->height = frame->height;

                    int ret = ffmpeg.av_frame_get_buffer(rgbaFrame, 32);
                    if (ret < 0)
                    {
                        _logger.LogError("Failed to allocate RGBA frame buffer: {Error}", ret);
                        return;
                    }

                    // 执行颜色空间转换
                    ret = ffmpeg.sws_scale(swsContext,
                        frame->data, frame->linesize, 0, frame->height,
                        rgbaFrame->data, rgbaFrame->linesize);

                    if (ret < 0)
                    {
                        _logger.LogError("Failed to convert frame: {Error}", ret);
                        return;
                    }

                    _logger.LogDebug("Frame converted to BGRA");

                    // 创建D3D11纹理
                    var texDesc = new Texture2DDescription
                    {
                        Width = frame->width,
                        Height = frame->height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Dynamic,
                        BindFlags = BindFlags.ShaderResource,
                        CpuAccessFlags = CpuAccessFlags.Write,
                        OptionFlags = ResourceOptionFlags.None
                    };

                    using (var texture = new Texture2D(_device, texDesc))
                    {
                        var dataBox = _context.MapSubresource(
                            texture, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);

                        try
                        {
                            // ���制数据
                            var sourcePtr = (IntPtr)rgbaFrame->data[0];
                            var sourceStride = rgbaFrame->linesize[0];
                            var destPtr = dataBox.DataPointer;
                            var destStride = dataBox.RowPitch;

                            for (int y = 0; y < frame->height; y++)
                            {
                                Utilities.CopyMemory(
                                    destPtr + y * destStride,
                                    sourcePtr + y * sourceStride,
                                    Math.Min(sourceStride, destStride));
                            }
                        }
                        finally
                        {
                            _context.UnmapSubresource(texture, 0);
                        }

                        _logger.LogDebug("Frame copied to D3D11 texture");
                        UpdateRenderTexture(texture);
                    }
                }
                finally
                {
                    var temp = rgbaFrame;
                    ffmpeg.av_frame_free(&temp);
                }
            }
            finally
            {
                ffmpeg.sws_freeContext(swsContext);
            }

            _logger.LogDebug("Frame rendering completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rendering frame");
        }
    }

    private void UpdateRenderTexture(D3D11.Texture2D sourceTexture)
    {
        try
        {
            if (_renderTexture == null || 
                _renderTexture.Description.Width != sourceTexture.Description.Width ||
                _renderTexture.Description.Height != sourceTexture.Description.Height)
            {
                RecreateRenderTexture(sourceTexture.Description.Width, sourceTexture.Description.Height);
            }

            // 复制到共享纹理
            _context.CopyResource(sourceTexture, _renderTexture);

            // 获取共享纹理的内容
            var stagingDesc = new D3D11.Texture2DDescription
            {
                Width = sourceTexture.Description.Width,
                Height = sourceTexture.Description.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = sourceTexture.Description.Format,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Staging,
                BindFlags = D3D11.BindFlags.None,
                CpuAccessFlags = D3D11.CpuAccessFlags.Read,
                OptionFlags = D3D11.ResourceOptionFlags.None
            };

            using (var stagingTexture = new D3D11.Texture2D(_device, stagingDesc))
            {
                // 复制到暂存纹理
                _context.CopyResource(_renderTexture, stagingTexture);

                // 锁定暂存纹理
                var dataBox = _context.MapSubresource(
                    stagingTexture, 
                    0, 
                    D3D11.MapMode.Read, 
                    D3D11.MapFlags.None);

                try
                {
                    // 更新 D3D9 表面
                    var d3d9DataRect = _d3d9Surface.LockRectangle(
                        new RawRectangle(0, 0, stagingDesc.Width, stagingDesc.Height),
                        D3D9.LockFlags.Discard);  // 使用 Discard 标志提高性能

                    try
                    {
                        // 复制数据
                        for (int y = 0; y < stagingDesc.Height; y++)
                        {
                            Utilities.CopyMemory(
                                d3d9DataRect.DataPointer + y * d3d9DataRect.Pitch,
                                dataBox.DataPointer + y * dataBox.RowPitch,
                                Math.Min(dataBox.RowPitch, d3d9DataRect.Pitch));
                        }
                    }
                    finally
                    {
                        _d3d9Surface.UnlockRectangle();
                    }
                }
                finally
                {
                    _context.UnmapSubresource(stagingTexture, 0);
                }
            }

            // 通知 D3DImage 更新
            _targetImage.Dispatcher.Invoke(() =>
            {
                _d3dImage.InvalidateD3DImage();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating render texture: {Error}", ex.Message);
        }
    }

    private void RecreateRenderTexture(int width, int height)
    {
        Utilities.Dispose(ref _renderShaderView);
        Utilities.Dispose(ref _renderTexture);

        var texDesc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.SharedKeyedmutex | ResourceOptionFlags.SharedNthandle  // 添加共享选项
        };

        _renderTexture = new Texture2D(_device, texDesc);
        _renderShaderView = new ShaderResourceView(_device, _renderTexture);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Utilities.Dispose(ref _d3d9Surface);
            Utilities.Dispose(ref _d3d9Device);
            Utilities.Dispose(ref _d3d9);
            
            Utilities.Dispose(ref _renderShaderView);
            Utilities.Dispose(ref _renderTexture);
            Utilities.Dispose(ref _context);
            Utilities.Dispose(ref _device);
            Utilities.Dispose(ref _factory);
            
            _cts.Dispose();
            _playbackSemaphore.Dispose();

            // 释放回调句柄
            if (_getFormatCallbackHandle.IsAllocated)
            {
                _getFormatCallbackHandle.Free();
            }

            _disposed = true;
            GC.SuppressFinalize(this);
            
            _logger.LogInformation("D3D11/D3D9 resources disposed");
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

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

    private unsafe void PlaybackLoop(CancellationToken token)
    {
        AVFormatContext* formatContext = null;
        AVCodecContext* codecContext = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        int reconnectAttempts = 0;
        const int maxReconnectAttempts = 3;
        bool needReconnect = true;
        int frameCount = 0;  // 添加帧计数器

        while (needReconnect && reconnectAttempts < maxReconnectAttempts && !token.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting playback loop for URL: {Url}", _url);

                // 设置网络选项
                AVDictionary* dict = null;
                ffmpeg.av_dict_set(&dict, "stimeout", "2000000", 0);  // 2秒超时
                ffmpeg.av_dict_set(&dict, "rtsp_transport", "tcp", 0);
                ffmpeg.av_dict_set(&dict, "buffer_size", "1024000", 0);
                ffmpeg.av_dict_set(&dict, "max_delay", "500000", 0);
                ffmpeg.av_dict_set(&dict, "reconnect", "1", 0);

                _logger.LogDebug("Opening input with options");
                int result = ffmpeg.avformat_open_input(&formatContext, _url, null, &dict);
                if (result < 0)
                {
                    LogFFmpegError("Failed to open input", result);
                    throw new FFmpegException("Failed to open input", result);
                }

                _logger.LogDebug("Finding stream info");
                if (ffmpeg.avformat_find_stream_info(formatContext, null) < 0)
                {
                    throw new FFmpegException("Failed to find stream info", -1);
                }

                var videoStream = FindVideoStream(formatContext);
                _logger.LogDebug("Found video stream at index: {Index}", videoStream);

                var codec = SetupCodec(formatContext, videoStream, out codecContext);
                _logger.LogDebug("Codec setup completed: {CodecName}", Marshal.PtrToStringAnsi((IntPtr)codec->name));

                frame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();

                _logger.LogInformation("Starting frame reading loop");
                int packetCount = 0;
                while (!token.IsCancellationRequested && _isPlaying)
                {
                    result = ffmpeg.av_read_frame(formatContext, packet);
                    if (result < 0)
                    {
                        LogFFmpegError("Error reading frame", result);
                        break;
                    }

                    if (packet->stream_index == videoStream)
                    {
                        packetCount++;
                        _logger.LogDebug("Reading packet #{Count}: pts={Pts}, dts={Dts}, size={Size}, flags={Flags}", 
                            packetCount, packet->pts, packet->dts, packet->size, packet->flags);

                        int ret = ffmpeg.avcodec_send_packet(codecContext, packet);
                        if (ret < 0)
                        {
                            LogFFmpegError("Error sending packet for decoding", ret);
                            continue;
                        }

                        _logger.LogDebug("Packet #{Count} sent to decoder", packetCount);

                        while (ret >= 0)
                        {
                            ret = ffmpeg.avcodec_receive_frame(codecContext, frame);
                            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                            {
                                _logger.LogDebug("Decoder needs more input for packet #{Count}", packetCount);
                                break;
                            }
                            if (ret == ffmpeg.AVERROR_EOF)
                            {
                                _logger.LogDebug("End of stream reached");
                                break;
                            }
                            if (ret < 0)
                            {
                                LogFFmpegError("Error receiving frame", ret);
                                break;
                            }

                            _logger.LogDebug("Frame decoded from packet #{Count}: format={Format}, width={Width}, height={Height}, " +
                                "pts={Pts}, key_frame={KeyFrame}, pict_type={PictType}, linesize={Linesize}", 
                                packetCount, frame->format, frame->width, frame->height,
                                frame->pts, frame->key_frame, frame->pict_type, frame->linesize[0]);

                            if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_DXVA2_VLD)
                            {
                                _logger.LogDebug("Processing hardware frame from packet #{Count}", packetCount);
                                ProcessHardwareFrame(frame);
                            }
                            else
                            {
                                _logger.LogDebug("Processing software frame from packet #{Count}", packetCount);
                                RenderD3D11Frame(frame);
                            }
                        }
                    }

                    ffmpeg.av_packet_unref(packet);
                }

                needReconnect = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in playback loop (attempt {Attempt}/{Max})", 
                    reconnectAttempts + 1, maxReconnectAttempts);
                if (!token.IsCancellationRequested)
                {
                    reconnectAttempts++;
                    Thread.Sleep(1000);
                }
                else
                {
                    needReconnect = false;
                }
            }
            finally
            {
                CleanupFFmpegResources(ref frame, ref packet, ref codecContext, ref formatContext);
            }
        }

        _isPlaying = false;
        OnStatusChanged?.Invoke(this, "Stopped");
    }

    private unsafe void CleanupFFmpegResources(
        ref AVFrame* frame,
        ref AVPacket* packet,
        ref AVCodecContext* codecContext,
        ref AVFormatContext* formatContext)
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
        try
        {
            var codecParams = formatContext->streams[videoStream]->codecpar;
            _logger.LogDebug("Setting up codec for codec_id: {CodecId}", codecParams->codec_id);

            // 先尝试查找硬件解码器
            var codec = ffmpeg.avcodec_find_decoder_by_name($"{ffmpeg.avcodec_get_name(codecParams->codec_id)}_dxva2");
            if (codec == null)
            {
                _logger.LogDebug("Hardware decoder not found, falling back to software decoder");
                codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
            }

            if (codec == null)
            {
                throw new InvalidOperationException("Codec not found");
            }

            _logger.LogDebug("Found codec: {CodecName}", Marshal.PtrToStringAnsi((IntPtr)codec->name));

            // 分配解码器上下文
            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
            {
                throw new InvalidOperationException("Failed to allocate codec context");
            }

            // 复制编解码器参数
            if (ffmpeg.avcodec_parameters_to_context(codecContext, codecParams) < 0)
            {
                throw new InvalidOperationException("Failed to copy codec params to codec context");
            }

            // 设置时间基准
            codecContext->time_base = formatContext->streams[videoStream]->time_base;
            codecContext->pkt_timebase = formatContext->streams[videoStream]->time_base;

            // 配置硬件加速
            if (ConfigureHardwareAcceleration(codecContext))
            {
                _logger.LogInformation("Hardware acceleration enabled");
            }
            else
            {
                _logger.LogInformation("Using software decoding");
            }

            // 打开解码器
            int ret = ffmpeg.avcodec_open2(codecContext, codec, null);
            if (ret < 0)
            {
                LogFFmpegError("Failed to open codec", ret);
                throw new InvalidOperationException("Failed to open codec");
            }

            _logger.LogInformation("Codec setup completed: {CodecName}", Marshal.PtrToStringAnsi((IntPtr)codec->name));
            return codec;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up codec");
            throw;
        }
    }

    private unsafe bool ConfigureHardwareAcceleration(AVCodecContext* codecContext)
    {
        try
        {
            _logger.LogDebug("Configuring hardware acceleration");

            // 创建 DXVA2 设备
            int result = ffmpeg.av_hwdevice_ctx_create(
                &codecContext->hw_device_ctx,
                AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,
                null,
                null,
                0);

            if (result < 0)
            {
                LogFFmpegError("Failed to create DXVA2 device", result);
                return false;
            }

            // 设置硬件帧上下文
            codecContext->hw_frames_ctx = CreateHWFramesContext(codecContext);
            if (codecContext->hw_frames_ctx == null)
            {
                _logger.LogError("Failed to create hardware frames context");
                return false;
            }

            // 设置获取格式的回调
            _getFormatCallback = GetHWFormat;
            _getFormatCallbackHandle = GCHandle.Alloc(_getFormatCallback);
            codecContext->get_format = new AVCodecContext_get_format_func() 
            { 
                Pointer = Marshal.GetFunctionPointerForDelegate(_getFormatCallback) 
            };

            // 设置线程计数
            codecContext->thread_count = 1;

            _logger.LogInformation("Hardware acceleration configured successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error configuring hardware acceleration");
            return false;
        }
    }

    private unsafe AVPixelFormat GetHWFormat(AVCodecContext* ctx, AVPixelFormat* pix_fmts)
    {
        _logger.LogDebug("GetHWFormat called, available formats:");
        AVPixelFormat* p;
        for (p = pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            _logger.LogDebug("Available format: {Format}", *p);
        }

        for (p = pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == AVPixelFormat.AV_PIX_FMT_DXVA2_VLD)
            {
                _logger.LogInformation("Selected DXVA2 hardware format");
                return AVPixelFormat.AV_PIX_FMT_DXVA2_VLD;
            }
        }

        _logger.LogWarning("Hardware format not available, falling back to software decoding");
        return pix_fmts[0];
    }

    private unsafe void ProcessVideoPacket(AVCodecContext* codecContext, AVFrame* frame, AVPacket* packet)
    {
        try
        {
            _logger.LogDebug("Processing packet: pts={Pts}, dts={Dts}, size={Size}", 
                packet->pts, packet->dts, packet->size);

            // 设置正确的时间戳
            packet->pts = packet->dts;

            int ret = ffmpeg.avcodec_send_packet(codecContext, packet);
            if (ret < 0)
            {
                LogFFmpegError("Error sending packet for decoding", ret);
                return;
            }

            while (true)
            {
                ret = ffmpeg.avcodec_receive_frame(codecContext, frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    break;
                }
                if (ret == ffmpeg.AVERROR_EOF)
                {
                    _logger.LogDebug("End of stream reached");
                    break;
                }
                if (ret < 0)
                {
                    LogFFmpegError("Error receiving frame", ret);
                    break;
                }

                _logger.LogDebug("Frame received: format={Format}, width={Width}, height={Height}, pts={Pts}", 
                    frame->format, frame->width, frame->height, frame->pts);

                if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_DXVA2_VLD)
                {
                    ProcessHardwareFrame(frame);
                }
                else
                {
                    RenderD3D11Frame(frame);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing video packet");
            OnError?.Invoke(this, ex);
        }
    }

    private unsafe void ProcessDecodedFrame(AVFrame* frame)
    {
        try
        {
            if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_DXVA2_VLD)
            {
                _logger.LogDebug("Processing DXVA2 frame");
                ProcessHardwareFrame(frame);
            }
            else
            {
                _logger.LogDebug("Processing software decoded frame");
                RenderD3D11Frame(frame);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing decoded frame");
        }
    }

    private unsafe void ProcessHardwareFrame(AVFrame* frame)
    {
        _logger.LogDebug("Starting hardware frame processing: format={Format}, hw_frames_ctx={HwCtx}", 
            frame->format, (IntPtr)frame->hw_frames_ctx);

        var swFrame = ffmpeg.av_frame_alloc();
        if (swFrame == null)
        {
            _logger.LogError("Failed to allocate software frame");
            return;
        }

        try
        {
            swFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            swFrame->width = frame->width;
            swFrame->height = frame->height;
            
            int ret = ffmpeg.av_frame_get_buffer(swFrame, 32);
            if (ret < 0)
            {
                LogFFmpegError("Failed to allocate SW frame buffer", ret);
                return;
            }

            _logger.LogDebug("Software frame buffer allocated: width={Width}, height={Height}, format={Format}",
                swFrame->width, swFrame->height, swFrame->format);

            if (frame->hw_frames_ctx == null)
            {
                _logger.LogError("No hardware frames context in frame");
                RenderD3D11Frame(frame);
                return;
            }

            _logger.LogDebug("Transferring frame from hardware to system memory");
            ret = ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0);
            if (ret < 0)
            {
                LogFFmpegError("Error transferring HW frame", ret);
                RenderD3D11Frame(frame);
                return;
            }

            _logger.LogDebug("Successfully transferred frame to system memory");
            RenderD3D11Frame(swFrame);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in hardware frame processing");
        }
        finally
        {
            var temp = swFrame;
            ffmpeg.av_frame_free(&temp);
        }
    }

    private unsafe void LogFFmpegError(string message, int errorCode)
    {
        var errorBuffer = new byte[1024];
        fixed (byte* errorPtr = errorBuffer)
        {
            ffmpeg.av_strerror(errorCode, errorPtr, (ulong)errorBuffer.Length);
            var errorMsg = Marshal.PtrToStringAnsi((IntPtr)errorPtr);
            _logger.LogError("{Message}: {Error} ({ErrorMessage})", message, errorCode, errorMsg);
        }
    }
}

// D3D11ImageSource 类用于在WPF中显示D3D11渲染内容
public class D3D11ImageSource : D3DImage
{
    private readonly object _lockObject = new object();
    private IntPtr _backBuffer;

    public void SetBackBuffer(IntPtr backBuffer)
    {
        lock (_lockObject)
        {
            try
            {
                _backBuffer = backBuffer;
                Lock();
                SetBackBuffer(D3DResourceType.IDirect3DSurface9, _backBuffer);
                AddDirtyRect(new Int32Rect(0, 0, PixelWidth, PixelHeight));
            }
            finally
            {
                Unlock();
            }
        }
    }

    public void InvalidateD3DImage()
    {
        if (IsFrontBufferAvailable && _backBuffer != IntPtr.Zero)
        {
            lock (_lockObject)
            {
                try
                {
                    Lock();
                    AddDirtyRect(new Int32Rect(0, 0, PixelWidth, PixelHeight));
                }
                finally
                {
                    Unlock();
                }
            }
        }
    }
} 