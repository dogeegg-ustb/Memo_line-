namespace MemolineDemo.Editing;

/// <summary>Copies complete straight RGBA pixels at mask positions, including transparent erasure.</summary>
public static class ImageCompositor
{
    public static void Replace(ImageRgba destination, DirtyImage image, bool before = false)
    {
        Validate(image, destination.Width, destination.Height);
        destination.Validate();
        var patch = before ? image.After : image.Now;
        foreach (var (x, y) in image.Mask.Pixels())
        {
            int source = checked(((y - image.Mask.Top) * image.Mask.Width + x - image.Mask.Left) * 4);
            int target = checked((y * destination.Width + x) * 4);
            patch.Bytes.AsSpan(source, 4).CopyTo(destination.Bytes.AsSpan(target, 4));
        }
    }

    public static void Validate(DirtyImage image, int canvasWidth, int canvasHeight)
    {
        image.After.Validate(); image.Now.Validate();
        var mask = image.Mask;
        if (mask.Left < 0 || mask.Top < 0 || (long)mask.Left + mask.Width > canvasWidth || (long)mask.Top + mask.Height > canvasHeight ||
            mask.Width <= 0 || mask.Height <= 0 || image.After.Width != mask.Width || image.After.Height != mask.Height ||
            image.Now.Width != mask.Width || image.Now.Height != mask.Height || mask.IsEmpty)
            throw new InvalidDataException("差异图片的尺寸、mask 或画布外框无效。");
    }

    public static void ValidateGroup(IReadOnlyList<DirtyImage> images, int width, int height)
    {
        var identities = new HashSet<DirtyImageKey>();
        foreach (var image in images)
        { Validate(image, width, height); if (!identities.Add(image.Key)) throw new InvalidDataException("同捕获组的图片 id 重复。"); }
        for (int i = 0; i < images.Count; i++)
            for (int j = i + 1; j < images.Count; j++)
            {
                var first = images[i]; var second = images[j];
                int left = Math.Max(first.Mask.Left, second.Mask.Left), top = Math.Max(first.Mask.Top, second.Mask.Top);
                int right = Math.Min(first.Mask.Left + first.Mask.Width, second.Mask.Left + second.Mask.Width);
                int bottom = Math.Min(first.Mask.Top + first.Mask.Height, second.Mask.Top + second.Mask.Height);
                for (int y = top; y < bottom; y++) for (int x = left; x < right; x++)
                {
                    if (!first.Mask.Contains(x, y) || !second.Mask.Contains(x, y)) continue;
                    int a = ((y - first.Mask.Top) * first.Mask.Width + x - first.Mask.Left) * 4;
                    int b = ((y - second.Mask.Top) * second.Mask.Width + x - second.Mask.Left) * 4;
                    if (!first.Now.Bytes.AsSpan(a, 4).SequenceEqual(second.Now.Bytes.AsSpan(b, 4)) ||
                        !first.After.Bytes.AsSpan(a, 4).SequenceEqual(second.After.Bytes.AsSpan(b, 4)))
                        throw new InvalidDataException("同捕获组的重叠补丁 RGBA 冲突。");
                }
            }
    }

    public static PixelMask Difference(ImageRgba actual, ImageRgba historical)
    {
        actual.Validate(); historical.Validate();
        if (actual.Width != historical.Width || actual.Height != historical.Height) throw new InvalidDataException("比较的图层尺寸不一致。");
        var result = new PixelMask(actual.Width, actual.Height);
        for (int y = 0; y < actual.Height; y++) for (int x = 0; x < actual.Width; x++)
        { int offset = (y * actual.Width + x) * 4; if (!actual.Bytes.AsSpan(offset, 4).SequenceEqual(historical.Bytes.AsSpan(offset, 4))) result.Set(x, y); }
        return result;
    }
}
