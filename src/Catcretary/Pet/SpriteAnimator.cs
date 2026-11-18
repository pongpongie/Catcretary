using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Catcretary.Services;

namespace Catcretary.Pet;

/// <summary>
/// 캐릭터 상태. 앞쪽 다섯은 앱이 정하는 의미 상태, 뒤쪽은 대기 중에 스스로 하는 행동이다.
/// manifest.json 의 clips 키와 이름이 같다. 클립이 없는 상태는 Idle 로 대신 그린다.
/// </summary>
public enum PetState
{
    Idle, Listening, Thinking, Done, Alert,
    Curious, Groom, Walk, Sleep, Scared,
}

/// <summary>Assets/pet/manifest.json 의 한 항목. 스프라이트시트 한 장에서 잘라낼 프레임 열.</summary>
public sealed class SpriteClip
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("frameWidth")] public int FrameWidth { get; set; } = 32;
    [JsonPropertyName("frameHeight")] public int FrameHeight { get; set; } = 32;
    /// <summary>시트에서 몇 번째 행인지 (0부터).</summary>
    [JsonPropertyName("row")] public int Row { get; set; }
    [JsonPropertyName("frames")] public int Frames { get; set; } = 1;
    [JsonPropertyName("fps")] public double Fps { get; set; } = 8;
    [JsonPropertyName("loop")] public bool Loop { get; set; } = true;
    /// <summary>true 면 항상 좌우 반전해서 그린다.</summary>
    [JsonPropertyName("flip")] public bool Flip { get; set; }
    /// <summary>시트의 캐릭터가 보는 방향. "right"(기본) 또는 "left". 걷는 방향에 맞춰 반전할 때 쓴다.</summary>
    [JsonPropertyName("facing")] public string Facing { get; set; } = "right";
}

public sealed class SpriteManifest
{
    [JsonPropertyName("clips")] public Dictionary<string, SpriteClip> Clips { get; set; } = new();
}

/// <summary>
/// 상태별 스프라이트 클립을 DispatcherTimer 로 넘긴다.
/// manifest 나 이미지가 없으면 Loaded=false 이고, 호출자는 placeholder 를 그린다.
/// </summary>
public sealed class SpriteAnimator
{
    private readonly Dictionary<PetState, (SpriteClip Clip, BitmapSource[] Frames)> _clips = new();
    private readonly DispatcherTimer _timer = new();
    private PetState _state = PetState.Idle;
    private int _index;
    private bool _flip;

    public bool Loaded => _clips.Count > 0;
    public bool Has(PetState state) => _clips.ContainsKey(state);
    public int FrameWidth { get; private set; } = 32;
    public int FrameHeight { get; private set; } = 32;

    /// <summary>(프레임, 좌우 반전 여부)</summary>
    public event Action<BitmapSource, bool>? FrameChanged;
    /// <summary>loop=false 클립이 끝까지 재생됐을 때.</summary>
    public event Action<PetState>? ClipFinished;

    public static string AssetDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "pet");

    public SpriteAnimator()
    {
        _timer.Tick += (_, _) => Advance();
    }

    public void Load()
    {
        _clips.Clear();
        var manifestPath = Path.Combine(AssetDirectory, "manifest.json");
        if (!File.Exists(manifestPath)) return;

        SpriteManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SpriteManifest>(File.ReadAllText(manifestPath));
        }
        catch (Exception ex)
        {
            AppLog.Error($"스프라이트 manifest 읽기 실패: {manifestPath}", ex);
            return;
        }
        if (manifest is null) return;

        var sheets = new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, clip) in manifest.Clips)
        {
            if (!Enum.TryParse<PetState>(name, ignoreCase: true, out var state)) continue;
            var path = Path.Combine(AssetDirectory, clip.File);
            if (!File.Exists(path)) continue;

            if (!sheets.TryGetValue(path, out var sheet))
            {
                sheet = new BitmapImage();
                sheet.BeginInit();
                sheet.CacheOption = BitmapCacheOption.OnLoad;
                sheet.UriSource = new Uri(path);
                sheet.EndInit();
                sheet.Freeze();
                sheets[path] = sheet;
            }

            var frames = new List<BitmapSource>();
            for (var i = 0; i < clip.Frames; i++)
            {
                var x = i * clip.FrameWidth;
                var y = clip.Row * clip.FrameHeight;
                if (x + clip.FrameWidth > sheet.PixelWidth || y + clip.FrameHeight > sheet.PixelHeight) break;
                var cropped = new CroppedBitmap(sheet, new System.Windows.Int32Rect(x, y, clip.FrameWidth, clip.FrameHeight));
                cropped.Freeze();
                frames.Add(cropped);
            }
            if (frames.Count == 0) continue;

            _clips[state] = (clip, frames.ToArray());
            FrameWidth = clip.FrameWidth;
            FrameHeight = clip.FrameHeight;
        }
    }

    /// <param name="faceRight">null 이면 클립 기본 방향. 걷기처럼 방향이 있는 동작은 여기로 맞춘다.</param>
    public void Play(PetState state, bool? faceRight = null)
    {
        if (!Loaded) return;
        if (!_clips.ContainsKey(state)) state = PetState.Idle;
        if (!_clips.ContainsKey(state)) return;

        _state = state;
        _index = 0;
        var (clip, frames) = _clips[state];

        var spriteFacesRight = !string.Equals(clip.Facing, "left", StringComparison.OrdinalIgnoreCase);
        _flip = clip.Flip;
        if (faceRight is { } fr) _flip ^= spriteFacesRight != fr;

        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, clip.Fps));
        FrameChanged?.Invoke(frames[0], _flip);
        if (frames.Length > 1) _timer.Start();
    }

    private void Advance()
    {
        var (clip, frames) = _clips[_state];
        _index++;
        if (_index >= frames.Length)
        {
            if (!clip.Loop)
            {
                _timer.Stop();
                _index = frames.Length - 1;
                ClipFinished?.Invoke(_state);
                return;
            }
            _index = 0;
        }
        FrameChanged?.Invoke(frames[_index], _flip);
    }
}
