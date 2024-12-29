using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using WpfVideoPlayer.Interfaces;
using D3D11= SharpDX.Direct3D11;

namespace WpfVideoPlayer
{
    public enum PlayerStatus
    {
        Playing,
        Paused,
        Stopped,
        Error
    }

    public class PlayerStatusEventArgs : EventArgs
    {
        public PlayerStatus Status { get; }

        public PlayerStatusEventArgs(PlayerStatus status)
        {
            Status = status;
        }
    }

    public class DX11VideoPlayer : IVideoPlayer
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly ILogger<DX11VideoPlayer> _logger;
        private readonly string _url;
        private readonly ImageSource _targetImage;
        private Task _playbackTask;
        private SemaphoreSlim _playbackSemaphore = new(1, 1);
        private bool _isPlaying;

        private D3D11.Device _d3dDevice;
        private DeviceContext _d3dContext;
        private Texture2D _renderTarget;
        private SwapChain _swapChain;

        private unsafe AVFormatContext* _formatContext;
        private unsafe AVCodecContext* _codecContext;
        private unsafe AVFrame* _frame;
        private unsafe AVPacket* _packet;
        private unsafe AVBufferRef* _hwDeviceCtx;

        private readonly object _syncLock = new();
        private readonly object _textureLock = new();
        private readonly object _frameLock = new();

        public event EventHandler<Exception> OnError;
        public event EventHandler<string> OnStatusChanged;

        public DX11VideoPlayer(Image targetImage, string url, ILogger<DX11VideoPlayer> logger)
        {
            _url = url;
            _targetImage = targetImage.Source;
            _logger = logger;
        }

        public async Task PlayAsync()
        {
            if (_isPlaying) return;

            await _playbackSemaphore.WaitAsync();
            try
            {
                _isPlaying = true;
                _playbackTask = Task.Run(() => PlaybackLoop(_cts.Token));
                _logger.LogInformation("Playback started");
            }
            finally
            {
                _playbackSemaphore.Release();
            }
        }

        private unsafe void PlaybackLoop(CancellationToken token)
        {
            try
            {
                InitializeFFmpeg();
                MainDecodingLoop(token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in playback loop");
                OnError?.Invoke(this, ex);
            }
            finally
            {
                CleanupResources();
            }
        }

        private unsafe void InitializeFFmpeg()
        {
            fixed (AVFormatContext** formatContextPtr = &_formatContext)
            fixed (AVFrame** framePtr = &_frame)
            fixed (AVPacket** packetPtr = &_packet)
            {
                *formatContextPtr = ffmpeg.avformat_alloc_context();
                *framePtr = ffmpeg.av_frame_alloc();
                *packetPtr = ffmpeg.av_packet_alloc();

                AVDictionary* options = null;
                ffmpeg.av_dict_set(&options, "rtsp_transport", "tcp", 0);
                ffmpeg.av_dict_set(&options, "buffer_size", "1024000", 0);
                ffmpeg.av_dict_set(&options, "fflags", "nobuffer", 0);
                ffmpeg.av_dict_set(&options, "flags", "low_delay", 0);

                int ret = ffmpeg.avformat_open_input(formatContextPtr, _url, null, &options);
                if (ret < 0)
                {
                    throw new FFmpegException("Failed to open input", ret);
                }

                if (ffmpeg.avformat_find_stream_info(*formatContextPtr, null) < 0)
                {
                    throw new FFmpegException("Failed to find stream info", -1);
                }

                int videoStreamIndex = FindVideoStream();
                SetupCodecContext(videoStreamIndex);
                InitializeHardwareAcceleration();
            }
        }

        private unsafe int FindVideoStream()
        {
            for (int i = 0; i < _formatContext->nb_streams; i++)
            {
                if (_formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    return i;
                }
            }
            throw new InvalidOperationException("No video stream found");
        }

        private unsafe void SetupCodecContext(int videoStreamIndex)
        {
            _logger.LogInformation("Setting up codec context...");
            
            var codecParameters = _formatContext->streams[videoStreamIndex]->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
            if (codec == null)
            {
                _logger.LogError($"Codec not found for ID: {codecParameters->codec_id}");
                throw new InvalidOperationException("Codec not found");
            }
            _logger.LogInformation($"Found codec: {Marshal.PtrToStringAnsi((IntPtr)codec->name)}");

            _codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecContext == null)
            {
                _logger.LogError("Failed to allocate codec context");
                throw new InvalidOperationException("Failed to allocate codec context");
            }
            _logger.LogInformation("Codec context allocated successfully");

            if (ffmpeg.avcodec_parameters_to_context(_codecContext, codecParameters) < 0)
            {
                _logger.LogError("Failed to copy codec parameters");
                throw new InvalidOperationException("Failed to copy codec parameters");
            }
            _logger.LogInformation("Codec parameters copied successfully");

            _codecContext->thread_count = Math.Min(16, Environment.ProcessorCount);
            _codecContext->thread_type = ffmpeg.FF_THREAD_FRAME;
            _codecContext->get_format = new AVCodecContext_get_format_func()
            {
                Pointer = Marshal.GetFunctionPointerForDelegate(GetHwFormat)
            };

            _logger.LogInformation("Opening codec...");
            InitializeHardwareAcceleration();
            if (ffmpeg.avcodec_open2(_codecContext, codec, null) < 0)
            {
                _logger.LogError("Failed to open codec");
                throw new InvalidOperationException("Failed to open codec");
            }
            _logger.LogInformation("Codec opened successfully");
        }

        private unsafe AVPixelFormat GetHwFormat(AVCodecContext* ctx, AVPixelFormat* fmt)
        {
            const AVPixelFormat hwPixFmt = AVPixelFormat.AV_PIX_FMT_D3D11;
            for (AVPixelFormat* p = fmt; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == hwPixFmt)
                {
                    return hwPixFmt;
                }
            }
            return AVPixelFormat.AV_PIX_FMT_NONE;
        }

        private unsafe void InitializeHardwareAcceleration()
        {
            lock (_syncLock)
            {
                _logger.LogInformation("Initializing hardware acceleration...");
                
                fixed (AVBufferRef** hwDeviceCtxPtr = &_hwDeviceCtx)
                {
                    _logger.LogInformation("Creating D3D11 device context...");
                    int ret = ffmpeg.av_hwdevice_ctx_create(hwDeviceCtxPtr, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
                    if (ret < 0)
                    {
                        _logger.LogError($"Failed to create hardware device context, error code: {ret}");
                        throw new FFmpegException("Failed to create hardware device context", ret);
                    }
                    _logger.LogInformation("D3D11 device context created successfully");

                    _logger.LogInformation("Setting hardware device context...");
                    _codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
                    if (_codecContext->hw_device_ctx == null)
                    {
                        _logger.LogError("Hardware device context is null");
                        throw new InvalidOperationException("Hardware device context is null");
                    }
                    _logger.LogInformation("Hardware device context set successfully");

                    _logger.LogInformation("Allocating hardware frames context...");
                    var hwFramesRef = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
                    if (hwFramesRef == null)
                    {
                        _logger.LogError("Failed to allocate hardware frames context");
                        throw new InvalidOperationException("Failed to allocate hardware frames context");
                    }
                    _logger.LogInformation("Hardware frames context allocated successfully");

                    var hwFramesCtx = (AVHWFramesContext*)hwFramesRef->data;
                    hwFramesCtx->format = AVPixelFormat.AV_PIX_FMT_D3D11;
                    hwFramesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
                    hwFramesCtx->width = _codecContext->width;
                    hwFramesCtx->height = _codecContext->height;
                    hwFramesCtx->initial_pool_size = 10;

                    _logger.LogInformation($"Configuring hardware frames: width={hwFramesCtx->width}, height={hwFramesCtx->height}, format={hwFramesCtx->format}, sw_format={hwFramesCtx->sw_format}");

                    var d3d11Params = (AVD3D11VAFramesContext*)hwFramesCtx->hwctx;
                    d3d11Params->BindFlags = (uint)(BindFlags.ShaderResource | BindFlags.RenderTarget);
                    d3d11Params->MiscFlags = (uint)ResourceOptionFlags.Shared;

                    _logger.LogInformation("Initializing hardware frames context...");
                    ret = ffmpeg.av_hwframe_ctx_init(hwFramesRef);
                    if (ret < 0)
                    {
                        _logger.LogError($"Failed to initialize hardware frames context, error code: {ret}");
                        ffmpeg.av_buffer_unref(&hwFramesRef);
                        throw new FFmpegException("Failed to initialize hardware frames context", ret);
                    }
                    _logger.LogInformation("Hardware frames context initialized successfully");

                    _logger.LogInformation("Setting hardware frames context...");
                    _codecContext->hw_frames_ctx = ffmpeg.av_buffer_ref(hwFramesRef);
                    ffmpeg.av_buffer_unref(&hwFramesRef);
                    if (_codecContext->hw_frames_ctx == null)
                    {
                        _logger.LogError("Failed to set hardware frames context");
                        throw new InvalidOperationException("Failed to set hardware frames context");
                    }
                    _logger.LogInformation("Hardware frames context set successfully");
                }

                _logger.LogInformation("Hardware acceleration initialized successfully");
            }
        }

        private unsafe void MainDecodingLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                int ret = ffmpeg.av_read_frame(_formatContext, _packet);
                if (ret < 0)
                {
                    if (ret == ffmpeg.AVERROR_EOF)
                    {
                        break;
                    }
                    continue;
                }

                if (_packet->stream_index == FindVideoStream())
                {
                    ProcessVideoPacket();
                }

                ffmpeg.av_packet_unref(_packet);
            }
        }

        private unsafe void ProcessVideoPacket()
        {
            if (_packet->stream_index != FindVideoStream())
            {
                return;
            }

            int ret = ffmpeg.avcodec_send_packet(_codecContext, _packet);
            if (ret < 0)
            {
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    return;
                }
                _logger.LogError($"Error sending packet for decoding: {ret}");
                ffmpeg.av_packet_unref(_packet);
                return;
            }

            while (true)
            {
                ret = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    break;
                }
                else if (ret < 0)
                {
                    _logger.LogError($"Error during decoding: {ret}");
                    break;
                }

                if (_frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
                {
                    RenderFrame();
                }
            }

            ffmpeg.av_packet_unref(_packet);
        }

        private unsafe void RenderFrame()
        {
            if (_frame->format != (int)AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                return;
            }

            lock (_frameLock)
            {
                if (_frame == null || _frame->data[0] == null)
                {
                    throw new InvalidOperationException("Frame data is null");
                }

                var ptr = new IntPtr(_frame->data[0]);
                if (ptr == IntPtr.Zero)
                {
                    throw new ArgumentNullException("Texture pointer is null");
                }

                lock (_textureLock)
                {
                    try
                    {
                        // 检查设备是否初始化
                        if (_d3dDevice == null)
                        {
                            throw new InvalidOperationException("D3D11 device not initialized");
                        }

                        // 创建Texture2D描述
                        var textureDesc = new D3D11.Texture2DDescription
                        {
                            Width = _codecContext->width,
                            Height = _codecContext->height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                            Usage = D3D11.ResourceUsage.Default,
                            BindFlags = D3D11.BindFlags.ShaderResource | D3D11.BindFlags.RenderTarget,
                            CpuAccessFlags = D3D11.CpuAccessFlags.None,
                            OptionFlags = D3D11.ResourceOptionFlags.Shared
                        };

                        // 验证纹理尺寸
                        if (textureDesc.Width <= 0 || textureDesc.Height <= 0)
                        {
                            throw new ArgumentException("Invalid texture dimensions");
                        }

                        // 创建Texture2D实例
                        using (var texture = (D3D11.Texture2D)Marshal.GetObjectForIUnknown(ptr))
                        {
                            // 创建渲染目标
                            using (var renderTarget = new D3D11.Texture2D(_d3dDevice, textureDesc))
                            {
                                // 创建渲染目标视图
                                using (var renderTargetView = new D3D11.RenderTargetView(_d3dDevice, renderTarget))
                                {
                                    // 设置渲染目标
                                    _d3dContext.OutputMerger.SetRenderTargets(renderTargetView);

                                    // 清除渲染目标
                                    _d3dContext.ClearRenderTargetView(renderTargetView, new RawColor4(0, 0, 0, 1));

                                    // 创建着色器资源视图
                                    using (var shaderResourceView = new D3D11.ShaderResourceView(_d3dDevice, texture))
                                    {
                                        // 设置着色器资源
                                        _d3dContext.PixelShader.SetShaderResource(0, shaderResourceView);

                                        // 绘制全屏四边形
                                        _d3dContext.Draw(4, 0);

                                        // 提交渲染命令
                                        _d3dContext.Flush();
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during frame rendering");
                        throw;
                    }
                }
            }
        }

        private unsafe void CleanupResources()
        {
            fixed (AVFrame** framePtr = &_frame)
            {
                if (*framePtr != null) ffmpeg.av_frame_free(framePtr);
            }
            fixed (AVPacket** packetPtr = &_packet)
            {
                if (*packetPtr != null) ffmpeg.av_packet_free(packetPtr);
            }
            fixed (AVCodecContext** codecContextPtr = &_codecContext)
            {
                if (*codecContextPtr != null) ffmpeg.avcodec_free_context(codecContextPtr);
            }
            fixed (AVFormatContext** formatContextPtr = &_formatContext)
            {
                if (*formatContextPtr != null) ffmpeg.avformat_close_input(formatContextPtr);
            }
            fixed (AVBufferRef** hwDeviceCtxPtr = &_hwDeviceCtx)
            {
                if (*hwDeviceCtxPtr != null) ffmpeg.av_buffer_unref(hwDeviceCtxPtr);
            }

            _d3dDevice?.Dispose();
            _d3dContext?.Dispose();
            _renderTarget?.Dispose();
            _swapChain?.Dispose();
        }

        public async Task StopAsync()
        {
            if (!_isPlaying) return;

            try
            {
                _cts.Cancel();
                await _playbackTask;
                _isPlaying = false;
                _logger.LogInformation("Playback stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during stop operation");
            }
        }

        public void Dispose()
        {
            StopAsync().Wait();
            _cts.Dispose();
            _playbackSemaphore.Dispose();
            _logger.LogInformation("Resources disposed");
        }

        public string Url => _url;
        public bool IsPlaying => _isPlaying;

        public async Task StartAsync()
        {
            await _playbackSemaphore.WaitAsync();
            try
            {
                _isPlaying = true;
                _playbackTask = Task.Run(() => PlaybackLoop(_cts.Token));
                _logger.LogInformation("Playback started");
                RaiseStatusChanged(PlayerStatus.Playing);
            }
            finally
            {
                _playbackSemaphore.Release();
            }
        }

        private void RaiseStatusChanged(PlayerStatus status)
        {
            OnStatusChanged?.Invoke(this, status.ToString());
        }
    }
}