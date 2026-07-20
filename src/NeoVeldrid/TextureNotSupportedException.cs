namespace NeoVeldrid;

/// <summary>
/// The exception thrown when a <see cref="ResourceFactory"/> is asked to create or wrap a texture description which
/// its owning <see cref="GraphicsDevice"/> does not support.
/// </summary>
public sealed class TextureNotSupportedException : NeoVeldridException
{
    internal TextureNotSupportedException(
        GraphicsBackend backendType,
        in TextureDescription description,
        TextureSupportResult supportResult)
        : base(GetMessage(backendType, supportResult))
    {
        BackendType = backendType;
        Description = description;
        SupportResult = supportResult;
    }

    /// <summary>
    /// Gets the backend which rejected the texture description.
    /// </summary>
    public GraphicsBackend BackendType { get; }

    /// <summary>
    /// Gets the complete rejected texture description.
    /// </summary>
    public TextureDescription Description { get; }

    /// <summary>
    /// Gets the structured capability result which caused creation to be rejected.
    /// </summary>
    public TextureSupportResult SupportResult { get; }

    private static string GetMessage(
        GraphicsBackend backendType,
        TextureSupportResult supportResult)
    {
        if (supportResult.Classification
            is TextureSupportClassification.InvalidDescription
            or TextureSupportClassification.LibraryContract)
        {
            return TextureDescriptionValidation.GetFailureMessage(
                supportResult.Reason);
        }

        return $"The {backendType} graphics device does not support the texture description: {supportResult}.";
    }
}
