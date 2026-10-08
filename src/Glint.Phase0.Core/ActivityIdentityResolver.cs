using System.Collections.Concurrent;
using System.Diagnostics;

namespace Glint.Phase0.Core;

/// <summary>
/// Works out which activity a look belongs to: which app, what kind of app,
/// and which thing inside it. Only these answers can start a new activity.
/// What is on screen never can, which is why a game's menus, maps and loading
/// screens stay part of the game.
/// </summary>
public sealed class ActivityIdentityResolver
{
    /// A page title can run to hundreds of characters (a search, a long
    /// document name); past this it stops helping anyone recognize it.
    internal const int MaxSubjectCharacters = 120;

    /// Looks needed before an unknown app's behaviour decides its mode.
    internal const int SamplesToDecide = 3;

    /// Readable characters below which a look is "mostly picture".
    internal const int SparseTextCharacters = 150;

    private static readonly ConcurrentDictionary<string, string> DisplayNames =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IActivityStore _store;

    public ActivityIdentityResolver(IActivityStore store)
    {
        _store = store;
    }

    public static string AppKeyOf(string processName) => "app:" + processName.ToLowerInvariant();

    public static string SiteKeyOf(string host) => "site:" + host.ToLowerInvariant();

    /// The app's own name ("Adobe Photoshop 2025", "Zen"), from its file
    /// description, falling back to the process name.
    public static string DisplayNameOf(string processName, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return processName;
        }

        return DisplayNames.GetOrAdd(executablePath, path =>
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                return string.IsNullOrWhiteSpace(description) || description.Length > 60
                    ? processName
                    : description;
            }
            catch (Exception error) when (error is FileNotFoundException or ArgumentException or IOException)
            {
                return processName;
            }
        });
    }

    /// <summary>
    /// The remembered profile of an app, deciding or refining it from the
    /// catalog and game signals. The user's choice is never touched.
    /// </summary>
    public AppProfile ResolveApp(ForegroundWindowInfo window, long nowMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(window);
        var key = AppKeyOf(window.ProcessName);
        var existing = _store.GetAppProfile(key);
        if (existing?.Source == ModeSource.User)
        {
            return existing;
        }

        var displayName = DisplayNameOf(window.ProcessName, window.ExecutablePath);
        AppProfile decided;
        if (ActivityCatalog.ForProcess(window.ProcessName) is { } known)
        {
            decided = new(key, displayName, known.Mode, known.Category, ModeSource.Catalog);
        }
        else if (ActivityCatalog.IsInGameFolder(window.ExecutablePath)
                 || ActivityCatalog.IsKnownToGameBar(window.ExecutablePath))
        {
            decided = new(key, displayName, ActivityMode.Play, ActivityCategory.Game, ModeSource.Catalog);
        }
        else if (ActivityCatalog.MakeFileIn(window.Title) is not null)
        {
            decided = new(key, displayName, ActivityMode.Make, ActivityCategory.Design, ModeSource.Catalog);
        }
        else if (existing is not null)
        {
            return existing;
        }
        else
        {
            decided = new(key, displayName, ActivityMode.Read, ActivityCategory.Other, ModeSource.Provisional);
        }

        decided = decided with
        {
            Samples = existing?.Samples ?? 0,
            SparseSamples = existing?.SparseSamples ?? 0,
            UpdatedAtMilliseconds = nowMilliseconds
        };
        if (existing is null
            || existing.Mode != decided.Mode
            || existing.Category != decided.Category
            || existing.Source != decided.Source
            || existing.DisplayName != decided.DisplayName)
        {
            _store.UpsertAppProfile(decided);
        }

        return decided;
    }

    /// A site's own profile, when the user set one or the catalog knows it.
    public AppProfile? ResolveSite(string? host, long nowMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var key = SiteKeyOf(host);
        var existing = _store.GetAppProfile(key);
        if (existing?.Source == ModeSource.User)
        {
            return existing;
        }

        if (ActivityCatalog.ForSite(host) is not { } known)
        {
            // Not in the catalog: whatever was learned from what played there.
            return existing;
        }

        var decided = new AppProfile(
            key,
            TitleNormalizer.SiteBrand(host),
            known.Mode,
            known.Category,
            ModeSource.Catalog,
            UpdatedAtMilliseconds: nowMilliseconds);
        if (existing is null || existing.Mode != decided.Mode || existing.Category != decided.Category)
        {
            _store.UpsertAppProfile(decided);
        }

        return decided;
    }

    /// <summary>
    /// Remembers that a site not in the catalog is for watching, after media
    /// was seen playing on it. Catalog entries and the user's choice stand.
    /// </summary>
    public AppProfile? LearnWatchSite(string? host, long nowMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(host) || ActivityCatalog.ForSite(host) is not null)
        {
            return null;
        }

        var key = SiteKeyOf(host);
        var existing = _store.GetAppProfile(key);
        if (existing?.Source == ModeSource.User || existing?.Mode == ActivityMode.Watch)
        {
            return existing;
        }

        var learned = new AppProfile(
            key,
            TitleNormalizer.SiteBrand(host),
            ActivityMode.Watch,
            ActivityCategory.Video,
            ModeSource.Learned,
            UpdatedAtMilliseconds: nowMilliseconds);
        _store.UpsertAppProfile(learned);
        return learned;
    }

    /// <summary>
    /// Learns an unknown app's mode from how it looks: an app that keeps
    /// showing almost no readable text while running full screen is a game.
    /// Decided once after a few looks, then remembered.
    /// </summary>
    public AppProfile Learn(AppProfile profile, int readableCharacters, bool fullscreen, long nowMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // Decided once: after that, nothing to learn and nothing to write.
        if (profile.Source != ModeSource.Provisional)
        {
            return profile;
        }

        var samples = profile.Samples + 1;
        var sparse = profile.SparseSamples
            + (readableCharacters < SparseTextCharacters && fullscreen ? 1 : 0);
        var updated = profile with
        {
            Samples = samples,
            SparseSamples = sparse,
            UpdatedAtMilliseconds = nowMilliseconds
        };
        if (samples >= SamplesToDecide)
        {
            updated = sparse * 2 > samples
                ? updated with { Mode = ActivityMode.Play, Category = ActivityCategory.Game, Source = ModeSource.Learned }
                : updated with { Source = ModeSource.Learned };
        }

        _store.UpsertAppProfile(updated);
        return updated;
    }

    /// <summary>
    /// The identity of one look. The subject depends on the kind of activity,
    /// so each one groups the way people describe it: one game, one file
    /// being edited, one inbox, one video, one page.
    /// </summary>
    public PageIdentity Identify(
        ForegroundWindowInfo window,
        AppProfile app,
        AppProfile? siteProfile,
        string? site)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(app);
        var mode = siteProfile?.Mode ?? app.Mode;
        var category = siteProfile?.Category ?? app.Category;
        var constant = TitleNormalizer.LearnConstantSegments(_store.GetRecentTitles(window.ProcessName));
        return Compose(window.ProcessName, app.DisplayName, window.Title, site, mode, category, constant);
    }

    /// Pure core of <see cref="Identify"/>, shared with the backfill of
    /// captures stored before activities existed.
    public static PageIdentity Compose(
        string processName,
        string appName,
        string title,
        string? site,
        ActivityMode mode,
        ActivityCategory category,
        IReadOnlySet<string>? constantSegments)
    {
        var cleaned = TitleNormalizer.Clean(title, constantSegments, site);
        var place = site is null ? appName : TitleNormalizer.SiteBrand(site);
        string subject;
        string? phase = null;
        switch (mode)
        {
            case ActivityMode.Play:
                subject = site is null ? appName : place;
                break;
            case ActivityMode.Private:
                subject = place;
                break;
            case ActivityMode.Make:
                var file = ActivityCatalog.MakeFileIn(title);
                subject = file ?? (site is null ? appName : place);
                phase = file is null ? NullIfSame(cleaned, subject) : null;
                break;
            case ActivityMode.Watch when category == ActivityCategory.Music:
                subject = place;
                phase = NullIfSame(cleaned, subject);
                break;
            default:
                switch (category)
                {
                    case ActivityCategory.Email:
                    case ActivityCategory.Chat:
                    case ActivityCategory.Files:
                        subject = place;
                        phase = NullIfSame(cleaned, subject);
                        break;
                    case ActivityCategory.Coding when site is null:
                        var parts = cleaned.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        subject = parts.Length >= 2 ? parts[^1] : (cleaned.Length > 0 ? cleaned : appName);
                        phase = parts.Length >= 2 ? parts[0] : null;
                        break;
                    default:
                        subject = cleaned.Length > 0 ? cleaned : place;
                        break;
                }

                break;
        }

        if (subject.Length > MaxSubjectCharacters)
        {
            subject = subject[..MaxSubjectCharacters].TrimEnd() + "…";
        }

        var appKey = processName.ToLowerInvariant();
        var key = $"{appKey}|{site ?? string.Empty}|{TitleNormalizer.KeyOf(subject)}";
        return new PageIdentity(
            key,
            appKey,
            appName,
            site,
            subject,
            mode == ActivityMode.Private ? null : phase,
            mode,
            category,
            TitleNormalizer.IsUnsaved(title));
    }

    private static string? NullIfSame(string value, string subject) =>
        value.Length == 0 || value.Equals(subject, StringComparison.OrdinalIgnoreCase) ? null : value;
}
