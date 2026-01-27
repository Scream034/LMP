namespace LMP.Core.Models;

public class PlaybackState
{
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
    public float Progress => Duration.TotalSeconds > 0 
        ? (float)(Position.TotalSeconds / Duration.TotalSeconds) 
        : 0f;
    public bool IsPlaying { get; set; }
    public bool IsSeeking { get; set; }
}
