using System;
using System.IO;
using System.Windows.Threading;
using NAudio.Wave;
using WindowsDynamicBar.Core.Logging;

namespace WindowsDynamicBar.Services.Audio;

public sealed class WasapiSpectrumService : IDisposable
{
    private const int BandCount = 25;
    private const int FftSize = 512;

    private WasapiLoopbackCapture? _capture;
    private readonly float[] _rawBands = new float[BandCount];
    private readonly float[] _smoothedBands = new float[BandCount];
    private readonly float[] _sampleBuffer = new float[FftSize];
    private readonly float[] _real = new float[FftSize];
    private readonly float[] _imag = new float[FftSize];
    private readonly float[] _window = new float[FftSize];
    private int _sampleCount;
    private readonly object _lock = new();

    private DispatcherTimer? _renderTimer;
    private bool _running;
    private bool _disposed;

    public event Action<float[]>? SpectrumChanged;

    public WasapiSpectrumService()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _window[i] = 0.5f * (1.0f - (float)Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
        }

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 fps
        };
        _renderTimer.Tick += OnRenderTick;
    }

    public void Start()
    {
        if (_running || _disposed) return;
        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            _running = true;
            _renderTimer?.Start();
        }
        catch (Exception ex)
        {
            Log.Debug("WASAPI loopback capture unavailable", ("error", ex.Message));
            Stop();
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _renderTimer?.Stop();

        try
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.StopRecording();
                _capture.Dispose();
                _capture = null;
            }
        }
        catch { }

        lock (_lock)
        {
            Array.Clear(_rawBands, 0, BandCount);
            Array.Clear(_smoothedBands, 0, BandCount);
            _sampleCount = 0;
        }

        SpectrumChanged?.Invoke(_smoothedBands);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            Log.Debug("WASAPI recording stopped with error", ("error", e.Exception.Message));
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _capture is null) return;

        var format = _capture.WaveFormat;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            int channels = Math.Max(1, format.Channels);
            int floatCount = e.BytesRecorded / 4;
            int samples = floatCount / channels;

            lock (_lock)
            {
                for (int i = 0; i < samples; i++)
                {
                    float left = BitConverter.ToSingle(e.Buffer, i * channels * 4);
                    _sampleBuffer[_sampleCount++] = left;

                    if (_sampleCount >= FftSize)
                    {
                        ComputeBands(_sampleBuffer);
                        _sampleCount = 0;
                    }
                }
            }
        }
        else if (format.BitsPerSample == 16)
        {
            int channels = Math.Max(1, format.Channels);
            int sampleCount = e.BytesRecorded / 2 / channels;

            lock (_lock)
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    short val = BitConverter.ToInt16(e.Buffer, i * channels * 2);
                    _sampleBuffer[_sampleCount++] = val / 32768.0f;

                    if (_sampleCount >= FftSize)
                    {
                        ComputeBands(_sampleBuffer);
                        _sampleCount = 0;
                    }
                }
            }
        }
    }

    private void ComputeBands(float[] samples)
    {
        int half = FftSize / 2;
        for (int i = 0; i < FftSize; i++)
        {
            _real[i] = samples[i] * _window[i];
            _imag[i] = 0f;
        }

        Fft(_real, _imag);

        for (int b = 0; b < BandCount; b++)
        {
            double lowFrac = Math.Pow((double)b / BandCount, 2.0);
            double highFrac = Math.Pow((double)(b + 1) / BandCount, 2.0);
            int binStart = Math.Clamp((int)(lowFrac * (half - 1)), 0, half - 1);
            int binEnd = Math.Clamp((int)(highFrac * (half - 1)) + 1, binStart + 1, half);

            float sum = 0f;
            int count = 0;
            for (int k = binStart; k < binEnd; k++)
            {
                sum += (float)Math.Sqrt(_real[k] * _real[k] + _imag[k] * _imag[k]);
                count++;
            }

            float energy = count > 0 ? (sum / count) * 0.15f : 0f;
            _rawBands[b] = Math.Clamp(energy, 0f, 1f);
        }
    }

    private static void Fft(float[] real, float[] imag)
    {
        int n = real.Length;
        int j = 0;
        for (int i = 0; i < n - 1; i++)
        {
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
            int k = n >> 1;
            while (k <= j)
            {
                j -= k;
                k >>= 1;
            }
            j += k;
        }

        for (int l = 1; l < n; l <<= 1)
        {
            float theta = -(float)Math.PI / l;
            float wtemp = (float)Math.Sin(0.5f * theta);
            float wpr = -2.0f * wtemp * wtemp;
            float wpi = (float)Math.Sin(theta);
            float wr = 1.0f;
            float wi = 0.0f;

            for (int m = 0; m < l; m++)
            {
                for (int i = m; i < n; i += l << 1)
                {
                    int i1 = i + l;
                    float tempr = wr * real[i1] - wi * imag[i1];
                    float tempi = wr * imag[i1] + wi * real[i1];
                    real[i1] = real[i] - tempr;
                    imag[i1] = imag[i] - tempi;
                    real[i] += tempr;
                    imag[i] += tempi;
                }
                float temp = wr;
                wr = temp * wpr - wi * wpi + wr;
                wi = wi * wpr + temp * wpi + wi;
            }
        }
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (!_running) return;

        lock (_lock)
        {
            for (int i = 0; i < BandCount; i++)
            {
                float target = _rawBands[i];
                if (target > _smoothedBands[i])
                {
                    // Fast attack
                    _smoothedBands[i] = _smoothedBands[i] * 0.25f + target * 0.75f;
                }
                else
                {
                    // Slow decay
                    _smoothedBands[i] = _smoothedBands[i] * 0.90f + target * 0.10f;
                }
            }
        }

        float[] copy = new float[BandCount];
        Array.Copy(_smoothedBands, copy, BandCount);
        SpectrumChanged?.Invoke(copy);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        if (_renderTimer is not null)
        {
            _renderTimer.Tick -= OnRenderTick;
            _renderTimer.Stop();
            _renderTimer = null;
        }
    }
}
