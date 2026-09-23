using Compositor.Core.Raster;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// Undo/redo over whole-document snapshots. Documents are immutable records sharing their
/// bitmaps, so an entry costs a few references, never pixel copies. Edits nest: only the outermost
/// <see cref="Begin"/>/<see cref="End"/> pair records an entry, and only when the document changed.
/// </summary>
public sealed class DocumentHistory
{
    public readonly record struct Snapshot(CanvasDocument? Document, Guid? ActiveLayerId, Guid Revision);

    private sealed record Entry(string Name, Snapshot Before, Snapshot After);

    private readonly List<Entry> _past = [];
    private readonly List<Entry> _future = [];
    private Guid _revision = Guid.NewGuid();
    private Guid _savedRevision;
    private Snapshot? _pending;
    private string _pendingName = "Edit";
    private int _depth;

    public DocumentHistory(int entryLimit = 100, long retainedByteLimit = 256L * 1024 * 1024)
    {
        EntryLimit = Math.Max(0, entryLimit);
        RetainedByteLimit = Math.Max(0, retainedByteLimit);
        _savedRevision = _revision;
    }

    public int EntryLimit { get; }
    public long RetainedByteLimit { get; }

    public bool CanUndo => _depth == 0 && _past.Count > 0;
    public bool CanRedo => _depth == 0 && _future.Count > 0;
    public string UndoName => _past.Count > 0 ? _past[^1].Name : string.Empty;
    public string RedoName => _future.Count > 0 ? _future[^1].Name : string.Empty;
    public bool IsModified => _revision != _savedRevision;
    public int UndoCount => _past.Count;

    /// <summary>True while inside a <see cref="Begin"/>/<see cref="End"/> pair.</summary>
    public bool IsEditing => _depth > 0;

    public void MarkSaved() => _savedRevision = _revision;

    /// <summary>Marks an externally recovered snapshot dirty so closing still asks the user to save it.</summary>
    public void MarkModified()
    {
        _revision = Guid.NewGuid();
    }

    public void Reset()
    {
        _past.Clear();
        _future.Clear();
        _pending = null;
        _depth = 0;
        _revision = Guid.NewGuid();
        _savedRevision = _revision;
    }

    public void Begin(string name, CanvasDocument? document, Guid? activeLayerId)
    {
        if (_depth == 0)
        {
            _pending = new Snapshot(document, activeLayerId, _revision);
            _pendingName = name;
        }

        _depth++;
    }

    public void End(CanvasDocument? document, Guid? activeLayerId)
    {
        if (_depth == 0)
        {
            return;
        }

        _depth--;
        if (_depth > 0 || _pending is not { } before)
        {
            return;
        }

        _pending = null;
        // Selecting, navigating, and no-op edits must preserve redo history.
        if (Equals(before.Document, document))
        {
            return;
        }

        _revision = Guid.NewGuid();
        _past.Add(new Entry(_pendingName, before, new Snapshot(document, activeLayerId, _revision)));
        _future.Clear();
        Trim(document);
    }

    public Snapshot? Undo()
    {
        if (!CanUndo)
        {
            return null;
        }

        var entry = _past[^1];
        _past.RemoveAt(_past.Count - 1);
        _future.Add(entry);
        _revision = entry.Before.Revision;
        Trim(entry.Before.Document);
        return entry.Before;
    }

    public Snapshot? Redo()
    {
        if (!CanRedo)
        {
            return null;
        }

        var entry = _future[^1];
        _future.RemoveAt(_future.Count - 1);
        _past.Add(entry);
        _revision = entry.After.Revision;
        Trim(entry.After.Document);
        return entry.After;
    }

    /// <summary>Bytes retained only by history, excluding bitmaps in the live document.</summary>
    public long RetainedBytes(CanvasDocument? current)
    {
        var seen = new HashSet<SKBitmap>(ReferenceEqualityComparer.Instance);
        foreach (var bitmap in Bitmaps(current))
        {
            seen.Add(bitmap);
        }

        long bytes = 0;
        foreach (var entry in _past.Concat(_future))
        {
            foreach (var snapshot in new[] { entry.Before, entry.After })
            {
                foreach (var bitmap in Bitmaps(snapshot.Document))
                {
                    if (seen.Add(bitmap))
                    {
                        bytes += (long)bitmap.RowBytes * bitmap.Height;
                    }
                }
            }
        }

        return bytes;
    }

    private static IEnumerable<SKBitmap> Bitmaps(CanvasDocument? document)
    {
        foreach (var layer in document?.Layers ?? [])
        {
            foreach (var asset in new[] { layer.Asset, layer.Mask?.Asset })
            {
                if (asset is null)
                {
                    continue;
                }

                // A snapshot-backed asset that was never materialized holds only its tiles.
                if (asset.HasContiguousPixels)
                {
                    yield return asset.Image;
                }

                foreach (var patch in asset.Raster?.Patches ?? [])
                {
                    yield return patch.Image;
                }

                yield return asset.Thumbnail;
            }
        }
    }

    private void Trim(CanvasDocument? current)
    {
        while (_past.Count + _future.Count > EntryLimit || RetainedBytes(current) > RetainedByteLimit)
        {
            if (_past.Count > 0)
            {
                _past.RemoveAt(0);
            }
            else if (_future.Count > 0)
            {
                _future.RemoveAt(0);
            }
            else
            {
                break;
            }
        }
    }
}
