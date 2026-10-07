namespace MemolineDemo.Editing;

/// <summary>Reconstructs any packet boundary using one cursor and cropped before pixels.</summary>
public sealed class LayerTimeline
{
    private readonly IReadOnlyList<LayerPacket> _packets;
    private readonly ImageRgba _current;
    public LayerTimeline(IReadOnlyList<LayerPacket> packets, ImageRgba current)
    {
        current.Validate();
        _packets = packets.Where(p => !p.IsRecordingBaseline).OrderBy(p => p.TriggerTicks).ToArray();
        _current = current;
        foreach (var packet in _packets) ImageCompositor.ValidateGroup(packet.Images, current.Width, current.Height);
    }

    public ImageRgba BeforePacket(string packetId)
    {
        int index = -1;
        for (int i = 0; i < _packets.Count; i++) if (_packets[i].PacketId == packetId) { index = i; break; }
        if (index < 0) throw new KeyNotFoundException("找不到历史捕获组：" + packetId);
        var cursor = _current.Copy();
        for (int i = _packets.Count - 1; i >= index; i--)
            foreach (var image in _packets[i].Images) ImageCompositor.Replace(cursor, image, before: true);
        return cursor;
    }

    public bool VerifyRoundTrip()
    {
        if (_packets.Count == 0) return true;
        var cursor = BeforePacket(_packets[0].PacketId);
        foreach (var packet in _packets) foreach (var image in packet.Images) ImageCompositor.Replace(cursor, image);
        return cursor.Bytes.AsSpan().SequenceEqual(_current.Bytes);
    }
}
