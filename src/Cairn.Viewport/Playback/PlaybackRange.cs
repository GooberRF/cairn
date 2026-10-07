namespace Cairn.Viewport;

/// <summary>
/// What a <see cref="PlaybackViewModel"/> covers: the playable range and ramps in ticks, and the key
/// times the previous/next-key buttons jump between (any order, duplicates allowed).
/// </summary>
public sealed record PlaybackRange(int StartTime, int EndTime, int RampIn, int RampOut, IReadOnlyCollection<int> KeyTimes);
