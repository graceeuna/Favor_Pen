using System;
using System.Threading;
using NAudio.Wave;

namespace FavorPen.Services;

/// <summary>
/// 기본 마이크 입력의 소음 크기를 실시간으로 측정한다.
///
/// NAudio <see cref="WaveInEvent"/> 로 16bit 모노 PCM 을 받아, 버퍼마다 RMS→dBFS 를 계산하고
/// 0~100 스케일의 <see cref="Level"/> 로 환산해 지수이동평균(EMA)으로 부드럽게 만든다.
/// 오디오 콜백은 백그라운드 스레드에서 오므로 <see cref="Level"/> 은 원자적으로 읽고 쓰며,
/// UI 는 이 값을 <see cref="System.Windows.Threading.DispatcherTimer"/> 로 폴링하면 된다
/// (크로스스레드 이벤트 마샬링 불필요).
///
/// 절대 음압(SPL) 보정은 하지 않는다. 교실 소음 신호등 용도이므로 상대 레벨이면 충분하다.
/// </summary>
public sealed class NoiseMonitor : IDisposable
{
    /// <summary>측정기 상태.</summary>
    public enum MonitorState { Stopped, Running, NoDevice, Error }

    private WaveInEvent? _waveIn;
    private long _levelBits; // double 을 원자적으로 담기 위한 비트 저장(Interlocked).
    private volatile MonitorState _state = MonitorState.Stopped;
    private bool _disposed;

    // 레벨 매핑: dBFS(≤0) 에 오프셋을 더해 실제 교실 dB 느낌의 값으로 보여 준다.
    // 절대 보정(SPL)은 아니며 이 마이크 기준 상대값이다. 대략 조용 ~50, 대화 ~70, 매우 시끄러움 ~90+.
    private const double SplOffset = 100.0;
    private const double SplFloor = 20.0, SplCeil = 110.0;
    // EMA 평활 계수(0~1). 클수록 반응 빠르고 작을수록 부드럽다.
    private const double Smoothing = 0.4;

    /// <summary>현재 소음 레벨(대략 dB, 20~110). 스레드 안전.</summary>
    public double Level => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _levelBits));

    /// <summary>현재 상태.</summary>
    public MonitorState State => _state;

    /// <summary>측정을 시작한다. 마이크가 없으면 <see cref="MonitorState.NoDevice"/> 로 남는다.</summary>
    public void Start()
    {
        if (_disposed || _state == MonitorState.Running)
            return;

        if (WaveInEvent.DeviceCount <= 0)
        {
            _state = MonitorState.NoDevice;
            return;
        }

        try
        {
            SetLevel(0);
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(44100, 16, 1), // 44.1kHz 16bit 모노
                BufferMilliseconds = 50,
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
            _state = MonitorState.Running;
        }
        catch
        {
            _state = MonitorState.Error;
            DisposeWaveIn();
        }
    }

    /// <summary>측정을 멈추고 마이크 장치를 해제한다.</summary>
    public void Stop()
    {
        if (_state != MonitorState.Running)
        {
            DisposeWaveIn();
            if (_state != MonitorState.NoDevice && _state != MonitorState.Error)
                _state = MonitorState.Stopped;
            SetLevel(0);
            return;
        }

        try { _waveIn?.StopRecording(); }
        catch { /* 무시 */ }
        // 실제 해제는 RecordingStopped 콜백에서.
        _state = MonitorState.Stopped;
        SetLevel(0);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int count = e.BytesRecorded / 2; // 16bit
        if (count <= 0)
            return;

        double sumSq = 0;
        for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            short s = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
            double v = s / 32768.0;
            sumSq += v * v;
        }

        double rms = Math.Sqrt(sumSq / count);
        // dBFS: 무음 방지를 위해 하한을 둔다. 오프셋을 더해 교실 dB 느낌의 값으로.
        double db = 20.0 * Math.Log10(Math.Max(rms, 1e-7));
        double target = Math.Clamp(db + SplOffset, SplFloor, SplCeil);

        double prev = Level;
        double next = prev + (target - prev) * Smoothing;
        SetLevel(next);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) => DisposeWaveIn();

    private void SetLevel(double v) =>
        Interlocked.Exchange(ref _levelBits, BitConverter.DoubleToInt64Bits(v));

    private void DisposeWaveIn()
    {
        if (_waveIn == null)
            return;

        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;
        try { _waveIn.Dispose(); }
        catch { /* 무시 */ }
        _waveIn = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        DisposeWaveIn();
    }
}
