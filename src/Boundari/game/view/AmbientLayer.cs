using System;
using Godot;

namespace Boundari.View;

/// <summary>
/// One ambient sound, looped without a seam: two players take turns, and near the end of
/// each pass the next one starts from the beginning while the two cross-fade. The file's own
/// fade-in and fade-out are skipped (SkipStart / SkipEnd), so they're never heard.
/// </summary>
[GlobalClass]
public partial class AmbientLayer : Node
{
    [Export]
    public AudioStream? Stream { get; set; }

    /// <summary>This sound's own volume, on top of the master volume.</summary>
    [Export(PropertyHint.Range, "-40,12,0.5,suffix:dB")]
    public float VolumeDb { get; set; }

    [Export]
    public AmbientPlaces Places { get; set; }

    /// <summary>Seconds cut from the start of the file (its fade-in).</summary>
    [ExportGroup("Looping")]
    [Export(PropertyHint.Range, "0,30,0.1,suffix:s")]
    public float SkipStart { get; set; }

    /// <summary>Seconds cut from the end of the file (its fade-out).</summary>
    [Export(PropertyHint.Range, "0,30,0.1,suffix:s")]
    public float SkipEnd { get; set; }

    /// <summary>How long the end of one pass blends into the start of the next.</summary>
    [Export(PropertyHint.Range, "0.5,15,0.1,suffix:s")]
    public float CrossfadeSeconds { get; set; } = 3f;

    private const float Silent = 0.002f;

    private readonly AudioStreamPlayer?[] _players = new AudioStreamPlayer?[2];
    private int _current;        // the player in its main stretch
    private bool _fading;        // the other one has started: they're cross-fading
    private float _loudness;     // followed towards the target

    /// <summary>Moves the volume towards this loudness (0..1) and keeps the loop going.</summary>
    public void Advance(float target, float follow, float masterDb)
    {
        _loudness += (target - _loudness) * follow;
        if (Stream is null)
            return;
        var current = _players[_current];
        if (_loudness < Silent)
        {
            if (current is not null && target < Silent)
                StopAll(); // quiet and staying quiet: free the players
            return;
        }

        double length = Stream.GetLength();
        double start = Math.Min(SkipStart, length * 0.4);
        double end = Math.Max(length - SkipEnd, start + 1);
        double fade = Math.Min(CrossfadeSeconds, (end - start) / 3);
        if (current is null || !current.Playing)
        {
            // Start somewhere random, so the loop's seam isn't always at the same moment.
            StopAll();
            current = Player(_current);
            current.Play((float)(start + GD.Randf() * Math.Max(end - fade - start, 0)));
        }

        double position = current.GetPlaybackPosition();
        float mine = 1, next = 0;
        if (position >= end - fade)
        {
            var other = Player(1 - _current);
            if (!_fading)
            {
                other.Play((float)start);
                _fading = true;
            }
            float t = (float)Math.Clamp((position - (end - fade)) / fade, 0, 1);
            mine = MathF.Cos(t * MathF.PI / 2); // equal power: the loudness holds through the blend
            next = MathF.Sin(t * MathF.PI / 2);
            if (t >= 1 || !current.Playing)
            {
                current.Stop();
                _current = 1 - _current;
                _fading = false;
                (mine, next) = (next, 0);
                current = other;
            }
        }
        current.VolumeDb = Db(_loudness * mine, masterDb);
        if (_fading && _players[1 - _current] is { } incoming)
            incoming.VolumeDb = Db(_loudness * next, masterDb);
    }


    private float Db(float gain, float masterDb) => Mathf.LinearToDb(Math.Max(gain, 1e-5f)) + VolumeDb + masterDb;

    private AudioStreamPlayer Player(int i)
    {
        if (_players[i] is not { } player)
        {
            player = new AudioStreamPlayer { Name = $"Loop{i}", Stream = Stream };
            AddChild(player);
            _players[i] = player;
        }
        if (player.Stream != Stream)
            player.Stream = Stream; // swapped in the inspector (setting it stops the player, so only then)
        return player;
    }

    private void StopAll()
    {
        foreach (var p in _players)
            p?.Stop();
        _fading = false;
    }
}
