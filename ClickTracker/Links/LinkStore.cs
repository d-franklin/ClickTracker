using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ClickTracker.Links;

/// <summary>
/// Owns the link definition file and the in-memory snapshot the redirect path reads. The file is the
/// source of truth; the snapshot is a cache of it.
/// </summary>
public sealed class LinkStore : IDisposable
{
    private const int ReloadDebounceMs = 250;

    private readonly string _filePath;
    private readonly ILogger<LinkStore> _logger;
    private readonly Lock _writeLock = new();

    private Dictionary<string, string> _snapshot = new(StringComparer.Ordinal);
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;

    public LinkStore(IOptions<LinksOptions> options, IHostEnvironment environment, ILogger<LinkStore> logger)
    {
        _filePath = Path.IsPathRooted(options.Value.FilePath)
            ? options.Value.FilePath
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.FilePath));
        _logger = logger;
    }

    /// <summary>The full path of the link definition file.</summary>
    public string FilePath => _filePath;

    /// <summary>Reads the file into the snapshot. A missing file starts the service with no links.</summary>
    public void Load()
    {
        var links = ReadFromDisk();
        if (links is null)
        {
            _logger.LogInformation("Link file {Path} does not exist, starting with no links", _filePath);
            links = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Volatile.Write(ref _snapshot, links);
        _logger.LogInformation("Loaded {Count} links from {Path}", links.Count, _filePath);
    }

    /// <summary>Watches the file and reloads the snapshot when it changes on disk.</summary>
    public void StartWatching()
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException($"Link file path '{_filePath}' has no directory.");
        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(_filePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;

        _logger.LogInformation("Watching {Path} for changes", _filePath);
    }

    /// <summary>Finds the target URL for a link ID.</summary>
    public bool TryGetTarget(string id, out string target)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.TryGetValue(id, out target!);
    }

    /// <summary>Lists every link, ordered by ID.</summary>
    public IReadOnlyList<Link> List()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot
            .Select(pair => new Link(pair.Key, pair.Value))
            .OrderBy(link => link.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Adds a link or replaces the target of an existing one.</summary>
    public void Upsert(string id, string url)
    {
        if (!LinkValidation.IsValidId(id))
        {
            throw new InvalidLinkException($"'{id}' is not a valid link ID.");
        }

        if (!LinkValidation.IsValidUrl(url))
        {
            throw new InvalidLinkException($"'{url}' is not an absolute HTTP or HTTPS URL.");
        }

        lock (_writeLock)
        {
            var updated = new Dictionary<string, string>(Volatile.Read(ref _snapshot), StringComparer.Ordinal)
            {
                [id] = url,
            };

            WriteToDisk(updated);
            Volatile.Write(ref _snapshot, updated);
        }

        _logger.LogInformation("Stored link {Id}", id);
    }

    /// <summary>Removes a link. Returns false when the ID is unknown.</summary>
    public bool Delete(string id)
    {
        lock (_writeLock)
        {
            var current = Volatile.Read(ref _snapshot);
            if (!current.ContainsKey(id))
            {
                return false;
            }

            var updated = new Dictionary<string, string>(current, StringComparer.Ordinal);
            updated.Remove(id);

            WriteToDisk(updated);
            Volatile.Write(ref _snapshot, updated);
        }

        _logger.LogInformation("Removed link {Id}", id);
        return true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _reloadTimer?.Dispose();
        _reloadTimer = null;
    }

    private Dictionary<string, string>? ReadFromDisk()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        var json = File.ReadAllText(_filePath);
        var links = JsonSerializer.Deserialize(json, LinksJsonContext.Default.DictionaryStringString);
        return links is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(links, StringComparer.Ordinal);
    }

    private void WriteToDisk(Dictionary<string, string> links)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException($"Link file path '{_filePath}' has no directory.");
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(links, LinksJsonContext.Default.DictionaryStringString);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, json + "\n");
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) => ScheduleReload();

    private void OnFileRenamed(object sender, RenamedEventArgs e) => ScheduleReload();

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "The watcher for {Path} failed", _filePath);
    }

    private void ScheduleReload()
    {
        _reloadTimer ??= new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _reloadTimer.Change(ReloadDebounceMs, Timeout.Infinite);
    }

    private void Reload()
    {
        try
        {
            var links = ReadFromDisk();
            if (links is null)
            {
                _logger.LogWarning("Link file {Path} disappeared, keeping the previous links", _filePath);
                return;
            }

            Volatile.Write(ref _snapshot, links);
            _logger.LogInformation("Reloaded {Count} links from {Path}", links.Count, _filePath);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not reload links from {Path}, keeping the previous links", _filePath);
        }
    }
}
