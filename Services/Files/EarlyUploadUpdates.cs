namespace CloudSelfService.Services.Files;

/// <summary>
/// Updates uploader.js reports for a file before the panel has its item. csspUploader.start queues the file and starts
/// the first one before it returns, so "queued" and "uploading" reach the panel ahead of the start result that creates
/// the item; without this they are lost and the item shows "Waiting to start" for the whole upload.
/// </summary>
public sealed class EarlyUploadUpdates
{
    /// <summary>A start that throws never claims its updates; this keeps that leftover small.</summary>
    public const int MaxHeldFiles = 64;

    /// <summary>The JSInvokable callbacks are callable with any id from the page, so one id cannot hold unbounded updates.</summary>
    public const int MaxChangesPerFile = 32;

    private sealed class Held
    {
        public readonly List<Action<UploadItem>> Changes = new();
        public LinkedListNode<string> Node = null!;
    }

    private readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();

    public int Count => this._held.Count;

    public void Hold(string id, Action<UploadItem> change)
    {
        if (!this._held.TryGetValue(id, out var held))
        {
            while (this._held.Count >= MaxHeldFiles && this._order.First is { } oldest)
            {
                this._order.RemoveFirst();
                this._held.Remove(oldest.Value);
            }
            held = new Held();
            held.Node = this._order.AddLast(id);
            this._held[id] = held;
        }
        // The newest updates carry the state the item should end in, so the oldest are the ones to drop.
        if (held.Changes.Count >= MaxChangesPerFile) held.Changes.RemoveAt(0);
        held.Changes.Add(change);
    }

    /// <summary>Applies the held updates for <paramref name="item"/> in the order they arrived, then forgets them.</summary>
    public void ApplyTo(UploadItem item)
    {
        if (!this._held.Remove(item.Id, out var held)) return;
        this._order.Remove(held.Node);
        foreach (var change in held.Changes)
            change(item);
    }
}
