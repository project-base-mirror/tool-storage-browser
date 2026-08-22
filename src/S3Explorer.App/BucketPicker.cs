using System.ComponentModel;
using S3Explorer.Core;

namespace S3Explorer.App;

/// <summary>Editable Bucket selector with cached and remote discovery support.</summary>
internal sealed class BucketPicker : UserControl
{
    private readonly ComboBox _comboBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems
    };
    private readonly Button _refreshButton = new()
    {
        Name = "RefreshBucketChoicesButton",
        Text = "刷新",
        AutoSize = true,
        MinimumSize = new Size(68, 30)
    };
    private readonly BucketDiscoveryCache _cache;
    private readonly Func<ConnectionProfile, CancellationToken, Task<IReadOnlyList<string>>>? _remoteLoader;
    private CancellationTokenSource? _refreshCancellation;
    private long _refreshGeneration;
    private bool _disposed;

    public BucketPicker(
        BucketDiscoveryCache cache,
        Func<ConnectionProfile, CancellationToken, Task<IReadOnlyList<string>>>? remoteLoader = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _remoteLoader = remoteLoader;
        _comboBox.Dock = DockStyle.Fill;
        _comboBox.Margin = new Padding(0, 2, 6, 2);
        _refreshButton.Dock = DockStyle.Fill;
        _refreshButton.Click += async (_, _) =>
        {
            if (SelectedProfile is not null) await RefreshAsync(SelectedProfile, preserve: true);
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(_comboBox, 0, 0);
        layout.Controls.Add(_refreshButton, 1, 0);
        Controls.Add(layout);
        MinimumSize = new Size(160, 34);
        Height = 34;
    }

    [Browsable(false)]
    public ConnectionProfile? SelectedProfile { get; private set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string BucketText
    {
        get => _comboBox.Text;
        set => _comboBox.Text = value ?? string.Empty;
    }

    [Browsable(false)]
    public string StatusText { get; private set; } = "未检查";

    public event EventHandler? StatusChanged;

    internal ComboBox Input => _comboBox;
    internal Button RefreshButton => _refreshButton;

    public async Task RefreshAsync(
        ConnectionProfile profile,
        bool preserve = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ThrowIfDisposed();
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var generation = ++_refreshGeneration;
        var refreshCancellation = _refreshCancellation;
        var token = _refreshCancellation.Token;
        SelectedProfile = profile;
        _refreshButton.Enabled = false;
        try
        {
            BucketDiscoverySnapshot? cached = null;
            string? cacheWarning = null;
            try
            {
                cached = await _cache.GetAsync(profile, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                cacheWarning = SafeMessage(exception);
            }

            if (!IsCurrentRefresh(generation, refreshCancellation, profile, token)) return;

            var names = MergeBucketNames(profile.KnownBuckets, cached?.Buckets ?? Array.Empty<string>());
            ApplyItems(names, preserve ? BucketText : string.Empty);
            if (_remoteLoader is null)
            {
                SetStatus(cacheWarning is not null
                    ? $"缓存读取失败，可手动输入：{cacheWarning}"
                    : cached is null
                        ? $"可手动输入；连接配置提供 {profile.KnownBuckets.Count} 个 Bucket"
                    : $"已加载上次缓存（{cached.Buckets.Count} 个，{cached.DiscoveredAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}）");
                return;
            }

            SetStatus(cached is null
                ? "未找到缓存，正在连接..."
                : $"已加载上次缓存（{cached.Buckets.Count} 个，{cached.DiscoveredAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}），正在刷新...");
            var remote = await _remoteLoader(profile, token).ConfigureAwait(true);
            if (!IsCurrentRefresh(generation, refreshCancellation, profile, token)) return;
            names = MergeBucketNames(profile.KnownBuckets, cached?.Buckets ?? Array.Empty<string>(), remote);
            ApplyItems(names, preserve ? BucketText : string.Empty);
            try
            {
                await _cache.RecordSuccessfulDiscoveryAsync(
                    profile,
                    remote,
                    cancellationToken: token).ConfigureAwait(true);
                if (IsCurrentRefresh(generation, refreshCancellation, profile, token))
                    SetStatus($"已连接并刷新（{names.Count} 个可选 Bucket）");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (IsCurrentRefresh(generation, refreshCancellation, profile, token))
                    SetStatus($"已连接并刷新（{names.Count} 个可选 Bucket）；缓存保存失败：{SafeMessage(exception)}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsCurrentRefresh(generation, refreshCancellation, profile, token))
                SetStatus($"刷新失败，已保留可用选项，可手动输入：{SafeMessage(exception)}");
        }
        finally
        {
            if (!_disposed && generation == _refreshGeneration &&
                ReferenceEquals(refreshCancellation, _refreshCancellation))
                _refreshButton.Enabled = _remoteLoader is not null;
        }
    }

    public static IReadOnlyList<string> MergeBucketNames(params IEnumerable<string>[] sources) => sources
        .SelectMany(static source => source ?? Array.Empty<string>())
        .Where(static item => !string.IsNullOrWhiteSpace(item))
        .Select(static item => item.Trim())
        .Where(static item => item.Length <= 255 && !item.Any(char.IsControl) && !item.Contains('/') && !item.Contains('\\'))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static item => item, StringComparer.Ordinal)
        .ToArray();

    private void ApplyItems(IReadOnlyList<string> names, string preservedText)
    {
        _comboBox.BeginUpdate();
        try
        {
            _comboBox.Items.Clear();
            _comboBox.Items.AddRange(names.Cast<object>().ToArray());
            _comboBox.Text = preservedText;
        }
        finally { _comboBox.EndUpdate(); }
    }

    private void SetStatus(string value)
    {
        if (_disposed) return;
        StatusText = value;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsCurrentRefresh(
        long generation,
        CancellationTokenSource refreshCancellation,
        ConnectionProfile profile,
        CancellationToken token) =>
        !_disposed &&
        !token.IsCancellationRequested &&
        generation == _refreshGeneration &&
        ReferenceEquals(refreshCancellation, _refreshCancellation) &&
        SelectedProfile?.Id == profile.Id;

    private static string SafeMessage(Exception exception)
    {
        var message = SensitiveDataRedactor.Redact(exception.Message).Trim();
        if (message.Length == 0) message = exception.GetType().Name;
        return message.Length <= 240 ? message : message[..240] + "...";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BucketPicker));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _refreshCancellation = null;
        }
        base.Dispose(disposing);
    }
}
