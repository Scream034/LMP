using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio;

public sealed partial class AudioPlayer
{
    private const int PostDeviceRecoveryStabilizationMs = 100;

    /// <summary>
    /// Обработчик потери устройства от pipeline.
    /// </summary>
    private void OnPipelineDeviceLost(AudioPipeline pipeline, int sessionId)
    {
        if (_disposed || _activePipeline != pipeline) return;
        if (_session.IsStale(sessionId)) return;

        CancelActiveSeek();
        StopTimers();

        Log.Warn("[AudioPlayer] Device lost — auto-pausing");
        SetState(PlayerState.Paused);
        _events.RaiseDeviceLost();
    }

    /// <summary>
    /// Обработчик появления устройства.
    /// </summary>
    private void OnPipelineDeviceAvailable(AudioPipeline pipeline, int sessionId)
    {
        if (_disposed || _activePipeline != pipeline) return;
        if (_session.IsStale(sessionId)) return;
        if (_state is not (PlayerState.Paused or PlayerState.Error)) return;

        Log.Info("[AudioPlayer] Device available — initiating auto-recovery");
        SetState(PlayerState.Buffering);
        _commandChannel.Writer.TryWrite(new DeviceRecoveryCommand(_session.Current));
    }

    /// <summary>
    /// Восстанавливает воспроизведение после потери устройства.
    /// </summary>
    private async Task HandleDeviceRecoveryAsync(DeviceRecoveryCommand cmd)
    {
        var pipeline = _activePipeline;
        if (pipeline == null || !pipeline.IsDeviceLost)
        {
            if (_state == PlayerState.Buffering) SetState(PlayerState.Paused);
            return;
        }

        try
        {
            await pipeline.RecoverFromDeviceLossAsync(
                CreateUrlRefresher(), _options, OnTrackEnded, HandleError,
                _lifetimeCts.Token).ConfigureAwait(false);

            if (_session.IsStale(cmd.SessionId) || _activePipeline != pipeline) return;
            _lifetimeCts.Token.ThrowIfCancellationRequested();

            await Task.Delay(PostDeviceRecoveryStabilizationMs, _lifetimeCts.Token).ConfigureAwait(false);
            if (_activePipeline != pipeline || _lifetimeCts.Token.IsCancellationRequested) return;

            int threshold = pipeline.SampleRate * pipeline.Channels * AudioConstants.MinSeekResumeBufferMs / 1000;
            await pipeline.WaitForBufferAsync(threshold, SeekWarmupTimeoutMs, _lifetimeCts.Token).ConfigureAwait(false);
            if (_activePipeline != pipeline || _lifetimeCts.Token.IsCancellationRequested) return;

            ResumePlaybackSequence(pipeline, startTimers: true, configurePipeline: true, trackId: _currentTrackId);

            Log.Info("[AudioPlayer] Device recovery complete");
            _events.RaiseDeviceRestored();
        }
        catch (AudioDeviceException ex)
        {
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(GetDeviceErrorMessage(), ex));
        }
        catch (OperationCanceledException)
        {
            if (_state == PlayerState.Buffering) SetState(PlayerState.Paused);
        }
        catch (Exception ex)
        {
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(ex.Message, ex));
        }
    }
}