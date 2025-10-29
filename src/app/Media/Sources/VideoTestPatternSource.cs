//-----------------------------------------------------------------------------
// Filename: VideoTestPatternSource.cs
//
// Description: Implements a video test pattern source based on a static 
// I420 file.
// Adds improved thread-safety, flexible overlay modes, optional custom overlay
// and a non-blocking max frame rate loop.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// History:
// 04 Sep 2020	Aaron Clauson	Created, Dublin, Ireland.
// 05 Nov 2020  Aaron Clauson   Added video encoder parameter.
// 10 Oct 2025  GitHub Copilot  Refactored for overlays, thread-safety & max rate loop.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorceryMedia.Abstractions;

namespace SIPSorcery.Media
{
    public class VideoTestPatternSource : IVideoSource, IDisposable
    {
        public const string TEST_PATTERN_RESOURCE_PATH = "SIPSorcery.media.testpattern.i420";
        public const int TEST_PATTERN_WIDTH = 640;
        public const int TEST_PATTERN_HEIGHT = 480;

        private const int VIDEO_SAMPLING_RATE = 90000;
        private const int MAXIMUM_FRAMES_PER_SECOND = 60;           // Threading.Timer practical max.
        private const int DEFAULT_FRAMES_PER_SECOND = 30;
        private const int MINIMUM_FRAMES_PER_SECOND = 1;
        private const int STAMP_BOX_SIZE = 20;
        private const int STAMP_BOX_PADDING = 10;
        private const int TIMER_DISPOSE_WAIT_MILLISECONDS = 1000;
        private const int VP8_SUGGESTED_FORMAT_ID = 96;
        private const int H264_SUGGESTED_FORMAT_ID = 100;

        public static ILogger logger = Sys.Log.Logger;

        public static readonly List<VideoFormat> SupportedFormats = new List<VideoFormat>
        {
            new VideoFormat(VideoCodecsEnum.VP8, VP8_SUGGESTED_FORMAT_ID, VIDEO_SAMPLING_RATE),
            new VideoFormat(VideoCodecsEnum.H264, H264_SUGGESTED_FORMAT_ID, VIDEO_SAMPLING_RATE, "packetization-mode=1")
        };

        private readonly object _syncLock = new object();
        private int _frameSpacing;
        private byte[] _testI420Buffer;
        private Timer _sendTestPatternTimer;
        private bool _isStarted;
        private bool _isPaused;
        private bool _isClosed;
        private bool _isMaxFrameRate;
        private int _frameCount;
        private IVideoEncoder _videoEncoder;
        private MediaFormatManager<VideoFormat> _formatManager;

        private CancellationTokenSource _maxFrameCts;
        private Task _maxFrameTask;

        public enum OverlayMode
        {
            None,
            MovingGrayBox,
            Timestamp
        }

        private OverlayMode _overlayMode = OverlayMode.MovingGrayBox;
        private Action<byte[], int, int, int> _customOverlayAction;

        /// <summary>
        /// Unencoded test pattern samples.
        /// </summary>
        public event RawVideoSampleDelegate OnVideoSourceRawSample;
#pragma warning disable CS0067
        public event RawVideoSampleFasterDelegate OnVideoSourceRawSampleFaster;
#pragma warning restore CS0067
        /// <summary>
        /// If a video encoder has been set then this event contains the encoded video
        /// samples.
        /// </summary>
        public event EncodedSampleDelegate OnVideoSourceEncodedSample;
        public event SourceErrorDelegate OnVideoSourceError;

        public int FrameRate => (_frameSpacing > 0) ? 1000 / _frameSpacing : DEFAULT_FRAMES_PER_SECOND;
        public bool IsStarted => _isStarted;
        public bool IsClosed => _isClosed;
        public bool IsMaxFrameRate => _isMaxFrameRate;

        public VideoTestPatternSource(IVideoEncoder encoder = null)
        {
            if (encoder != null)
            {
                _videoEncoder = encoder;
                _formatManager = new MediaFormatManager<VideoFormat>(SupportedFormats);
            }

            var assem = typeof(VideoTestPatternSource).GetTypeInfo().Assembly;
            var testPatternStm = assem.GetManifestResourceStream(TEST_PATTERN_RESOURCE_PATH);

            if (testPatternStm == null)
            {
                OnVideoSourceError?.Invoke($"Test pattern embedded resource could not be found, {TEST_PATTERN_RESOURCE_PATH}.");
            }
            else
            {
                _testI420Buffer = new byte[TEST_PATTERN_WIDTH * TEST_PATTERN_HEIGHT * 3 / 2];
#if NET9_0_OR_GREATER
                testPatternStm.ReadExactly(_testI420Buffer, 0, _testI420Buffer.Length);
#else
                // Ensure full buffer read.
                int offset = 0; int remaining = _testI420Buffer.Length; int read;
                while (remaining > 0 && (read = testPatternStm.Read(_testI420Buffer, offset, remaining)) > 0)
                {
                    offset += read; remaining -= read;
                }
#endif
                testPatternStm.Close();
                _sendTestPatternTimer = new Timer(GenerateTestPattern, null, Timeout.Infinite, Timeout.Infinite);
                _frameSpacing = 1000 / DEFAULT_FRAMES_PER_SECOND;
            }
        }

        public void RestrictFormats(Func<VideoFormat, bool> filter) => _formatManager?.RestrictFormats(filter);
        public List<VideoFormat> GetVideoSourceFormats() => _formatManager?.GetSourceFormats();
        public void SetVideoSourceFormat(VideoFormat videoFormat) => _formatManager?.SetSelectedFormat(videoFormat);
        public List<VideoFormat> GetVideoSinkFormats() => _formatManager?.GetSourceFormats();
        public void SetVideoSinkFormat(VideoFormat videoFormat) => _formatManager?.SetSelectedFormat(videoFormat);

        public void ForceKeyFrame() => _videoEncoder?.ForceKeyFrame();
        public bool HasEncodedVideoSubscribers() => OnVideoSourceEncodedSample != null;

        public void ExternalVideoSourceRawSample(uint durationMilliseconds, int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat) =>
            throw new NotImplementedException("The test pattern video source does not offer any encoding services for external sources.");

        public void ExternalVideoSourceRawSampleFaster(uint durationMilliseconds, RawImage rawImage) =>
            throw new NotImplementedException("The test pattern video source does not offer any encoding services for external sources.");

        public Task<bool> InitialiseVideoSourceDevice() =>
            throw new NotImplementedException("The test pattern video source does not use a device.");
        public bool IsVideoSourcePaused() => _isPaused;

        public void SetFrameRate(int framesPerSecond)
        {
            if (framesPerSecond < MINIMUM_FRAMES_PER_SECOND || framesPerSecond > MAXIMUM_FRAMES_PER_SECOND)
            {
                logger.LogWarning("{FramesPerSecond} frames per second not in the allowed range of {MinimumFramesPerSecond} to {MaximumFramesPerSecond}, ignoring.", framesPerSecond, MINIMUM_FRAMES_PER_SECOND, MAXIMUM_FRAMES_PER_SECOND);
                return;
            }
            _frameSpacing = 1000 / framesPerSecond;
            if (_isStarted && !_isMaxFrameRate)
            {
                _sendTestPatternTimer?.Change(0, _frameSpacing);
            }
        }

        public void SetOverlayMode(OverlayMode mode) => _overlayMode = mode;
        public void SetCustomOverlay(Action<byte[], int, int, int> overlayAction)
        {
            _customOverlayAction = overlayAction;
            if (overlayAction != null)
            {
                _overlayMode = OverlayMode.None; // custom overrides.
            }
        }

        /// <summary>
        /// Enables generation at max possible rate using a tight loop task (non-blocking caller thread).
        /// </summary>
        public void SetMaxFrameRate(bool isMaxFrameRate)
        {
            if (_isMaxFrameRate == isMaxFrameRate) return;
            _isMaxFrameRate = isMaxFrameRate;

            if (!_isStarted) return;

            if (_isMaxFrameRate)
            {
                _sendTestPatternTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _maxFrameCts = new CancellationTokenSource();
                _maxFrameTask = Task.Run(() => GenerateMaxFramesLoop(_maxFrameCts.Token));
            }
            else
            {
                _maxFrameCts?.Cancel();
                _sendTestPatternTimer?.Change(0, _frameSpacing);
            }
        }

        public Task PauseVideo()
        {
            _isPaused = true;
            _sendTestPatternTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            return Task.CompletedTask;
        }

        public Task ResumeVideo()
        {
            _isPaused = false;
            if (_isStarted)
            {
                if (_isMaxFrameRate)
                {
                    _maxFrameCts?.Cancel();
                    _maxFrameCts = new CancellationTokenSource();
                    _maxFrameTask = Task.Run(() => GenerateMaxFramesLoop(_maxFrameCts.Token));
                }
                else
                {
                    _sendTestPatternTimer?.Change(0, _frameSpacing);
                }
            }
            return Task.CompletedTask;
        }

        public Task StartVideo()
        {
            if (_isStarted) return Task.CompletedTask;
            _isStarted = true;
            if (_isMaxFrameRate)
            {
                _maxFrameCts = new CancellationTokenSource();
                _maxFrameTask = Task.Run(() => GenerateMaxFramesLoop(_maxFrameCts.Token));
            }
            else
            {
                _sendTestPatternTimer?.Change(0, _frameSpacing);
            }
            return Task.CompletedTask;
        }

        public Task CloseVideo()
        {
            if (_isClosed) return Task.CompletedTask;
            _isClosed = true;
            _maxFrameCts?.Cancel();
            ManualResetEventSlim mre = new ManualResetEventSlim();
            _sendTestPatternTimer?.Dispose(mre.WaitHandle);
            return Task.Run(() => mre.Wait(TIMER_DISPOSE_WAIT_MILLISECONDS));
        }

        private void GenerateMaxFramesLoop(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            long last = sw.ElapsedMilliseconds;
            while (!_isClosed && _isMaxFrameRate && !ct.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    Thread.Sleep(10);
                    last = sw.ElapsedMilliseconds;
                    continue;
                }
                var now = sw.ElapsedMilliseconds;
                _frameSpacing = (int)Math.Max(1, now - last);
                last = now;
                GenerateTestPattern(null);
                // Yield occasionally to avoid CPU monopolization.
                if ((_frameCount & 0xF) == 0)
                {
                    Thread.Yield();
                }
            }
        }

        private void GenerateTestPattern(object state)
        {
            RawVideoSampleDelegate rawHandler;
            EncodedSampleDelegate encodedHandler;
            lock (_syncLock)
            {
                rawHandler = OnVideoSourceRawSample;
                encodedHandler = OnVideoSourceEncodedSample;
                if (_isClosed || (_testI420Buffer == null) || (rawHandler == null && encodedHandler == null)) return;
                _frameCount++;

                // Apply overlay.
                ApplyOverlay(_testI420Buffer, TEST_PATTERN_WIDTH, TEST_PATTERN_HEIGHT, _frameCount);

                if (rawHandler != null)
                {
                    GenerateRawSample(TEST_PATTERN_WIDTH, TEST_PATTERN_HEIGHT, _testI420Buffer, rawHandler);
                }

                if (_videoEncoder != null && encodedHandler != null && _formatManager != null && !_formatManager.SelectedFormat.IsEmpty())
                {
                    var encodedBuffer = _videoEncoder.EncodeVideo(TEST_PATTERN_WIDTH, TEST_PATTERN_HEIGHT, _testI420Buffer, VideoPixelFormatsEnum.I420, _formatManager.SelectedFormat.Codec);
                    if (encodedBuffer != null)
                    {
                        uint fps = (_frameSpacing > 0) ? (uint)Math.Max(1, 1000 / _frameSpacing) : DEFAULT_FRAMES_PER_SECOND;
                        uint durationRtpTS = VIDEO_SAMPLING_RATE / fps;
                        encodedHandler.Invoke(durationRtpTS, encodedBuffer);
                    }
                }

                if (_frameCount == int.MaxValue) _frameCount = 0;
            }
        }

        private void GenerateRawSample(int width, int height, byte[] i420Buffer, RawVideoSampleDelegate handler)
        {
            var bgr = PixelConverter.I420toBGR(i420Buffer, width, height, out _);
            handler?.Invoke((uint)_frameSpacing, width, height, bgr, VideoPixelFormatsEnum.Bgr);
        }

        private void ApplyOverlay(byte[] buffer, int width, int height, int frameNumber)
        {
            if (_customOverlayAction != null)
            {
                _customOverlayAction(buffer, width, height, frameNumber);
                return;
            }
            switch (_overlayMode)
            {
                case OverlayMode.None:
                    return;
                case OverlayMode.MovingGrayBox:
                    StampI420Buffer(buffer, width, height, frameNumber);
                    break;
                case OverlayMode.Timestamp:
                    StampTimestamp(buffer, width, height, frameNumber);
                    break;
            }
        }

        /// <summary>
        /// Varying grey scale square.
        /// </summary>
        public static void StampI420Buffer(byte[] i420Buffer, int width, int height, int frameNumber)
        {
            int startX = width - STAMP_BOX_SIZE - STAMP_BOX_PADDING;
            int startY = height - STAMP_BOX_SIZE - STAMP_BOX_PADDING;
            byte val = (byte)(frameNumber % 255);
            for (int y = startY; y < startY + STAMP_BOX_SIZE && y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = startX; x < startX + STAMP_BOX_SIZE && x < width; x++)
                {
                    i420Buffer[rowOffset + x] = val; // Y plane only for simplicity.
                }
            }
        }

        /// <summary>
        /// Simple timestamp overlay (HH:MM:SS) encoded as bright blocks in Y plane.
        /// </summary>
        private void StampTimestamp(byte[] i420Buffer, int width, int height, int frameNumber)
        {
            string text = DateTime.UtcNow.ToString("HH:mm:ss") + $" #{frameNumber}";
            // Very primitive mono-spaced block representation: each character 6x8 pixels.
            int charW = 6; int charH = 8;
            int maxChars = Math.Min(text.Length, (width - 2 * STAMP_BOX_PADDING) / charW);
            int startX = STAMP_BOX_PADDING;
            int startY = height - charH - STAMP_BOX_PADDING;
            for (int i = 0; i < maxChars; i++)
            {
                char c = text[i];
                uint hash = (uint)c;
                for (int cy = 0; cy < charH; cy++)
                {
                    int y = startY + cy;
                    if (y >= height) break;
                    int row = y * width;
                    for (int cx = 0; cx < charW; cx++)
                    {
                        int x = startX + i * charW + cx;
                        if (x >= width) break;
                        // Use bits from hash to decide brightness; just for movement.
                        bool set = ((hash >> (cx + cy)) & 0x1) == 0x1;
                        i420Buffer[row + x] = set ? (byte)235 : (byte)40; // luma levels.
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_isClosed) return;
            _isClosed = true;
            try
            {
                _maxFrameCts?.Cancel();
                _sendTestPatternTimer?.Dispose();
                _videoEncoder?.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Dispose error in VideoTestPatternSource");
            }
        }
    }
}
