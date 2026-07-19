namespace NeoVeldrid.OpenGL.NoAllocEntryList;

internal struct NoAllocUpdateTextureEntry
{
    public readonly Tracked<Texture> Texture;
    public readonly StagingBlock StagingBlock;
    public readonly uint X;
    public readonly uint Y;
    public readonly uint Z;
    public readonly uint Width;
    public readonly uint Height;
    public readonly uint Depth;
    public readonly uint MipLevel;
    public readonly uint ArrayLayer;

    public NoAllocUpdateTextureEntry(
        Tracked<Texture> texture,
        StagingBlock stagingBlock,
        uint x,
        uint y,
        uint z,
        uint width,
        uint height,
        uint depth,
        uint mipLevel,
        uint arrayLayer)
    {
        Texture = texture;
        StagingBlock = stagingBlock;
        X = x;
        Y = y;
        Z = z;
        Width = width;
        Height = height;
        Depth = depth;
        MipLevel = mipLevel;
        ArrayLayer = arrayLayer;
    }
}
