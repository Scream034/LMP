namespace LMP.UI.Helpers;

internal enum PlaybackRecoveryMode
{
    AwaitUserAction,
    SkipAndPlay,
    SkipAndPause,
    Stop
}

internal readonly record struct EffectivePlaybackErrorPolicy(
    bool ShowNotification,
    PlaybackRecoveryMode RecoveryMode)
{
    public bool RequiresUserAction => RecoveryMode == PlaybackRecoveryMode.AwaitUserAction;
}

internal static class PlaybackErrorBehaviorMatrix
{
    public static bool UsesPlaybackFailureBehavior(PlaybackErrorBehavior errorBehavior) =>
        errorBehavior != PlaybackErrorBehavior.Dialog;

    public static EffectivePlaybackErrorPolicy Resolve(
        PlaybackErrorBehavior errorBehavior,
        PlaybackFailureBehavior failureBehavior)
    {
        return errorBehavior switch
        {
            PlaybackErrorBehavior.Dialog => new(true, PlaybackRecoveryMode.AwaitUserAction),
            PlaybackErrorBehavior.ToastAndSkip => ResolveAutomatic(showNotification: true, failureBehavior),
            PlaybackErrorBehavior.Ignore => ResolveAutomatic(showNotification: false, failureBehavior),
            _ => ResolveAutomatic(showNotification: true, PlaybackFailureBehavior.SkipAndPause)
        };
    }

    private static EffectivePlaybackErrorPolicy ResolveAutomatic(
        bool showNotification,
        PlaybackFailureBehavior failureBehavior)
    {
        return failureBehavior switch
        {
            PlaybackFailureBehavior.SkipAndPlay => new(showNotification, PlaybackRecoveryMode.SkipAndPlay),
            PlaybackFailureBehavior.SkipAndPause => new(showNotification, PlaybackRecoveryMode.SkipAndPause),
            PlaybackFailureBehavior.Stop => new(showNotification, PlaybackRecoveryMode.Stop),
            _ => new(showNotification, PlaybackRecoveryMode.SkipAndPause)
        };
    }
}